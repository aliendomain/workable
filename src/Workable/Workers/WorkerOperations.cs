using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkerOperations : IWorkerOperations, IWorkQuery, IDisposable
{
    private readonly WorkSystemCatalog catalog;
    private readonly WorkerEventPublisher workerEvents;
    private readonly IWorkerExecutionStrategy executionStrategy;
    private readonly ConcurrentDictionary<WorkerId, WorkerRecord> workers = [];
    private readonly WorkerIndex index = new();
    private readonly Lock lifecycleSync = new();
    private readonly Lock subjectSync = new();
    private readonly List<CancellationTokenSource> retiredSystemExecutionLifetimes = [];
    private readonly WorkerDispatcher dispatcher;
    private readonly WorkConcurrencyCoordinator concurrency;
    private readonly WorkerRetentionScheduler retention;
    private readonly IDotNetWorkOriginProvider dotNetOriginProvider;
    private readonly TimeSpan shutdownGracePeriod;
    private CancellationTokenSource systemExecutionLifetime = new();
    private volatile bool acceptingWork = true;

    internal WorkerOperations(
        WorkSystemCatalog catalog,
        WorkSystemId workSystemId,
        string? workSystemName,
        IServiceProvider rootServices,
        WorkEventStream events,
        IDotNetWorkOriginProvider dotNetOriginProvider,
        IReadOnlyList<WorkExceptionClassifier> systemExceptionClassifiers,
        IReadOnlyList<WorkExceptionClassifier> globalExceptionClassifiers,
        TimeSpan shutdownGracePeriod)
    {
        this.catalog = catalog;
        this.dotNetOriginProvider = dotNetOriginProvider;
        this.shutdownGracePeriod = shutdownGracePeriod;
        this.workerEvents = new WorkerEventPublisher(workSystemId, events, this.index.Synchronize);
        var logger = rootServices.GetService<ILoggerFactory>()?.CreateLogger("Workable.WorkerExecution");
        var invoker = new WorkerExecutionInvoker(
            workSystemId,
            workSystemName,
            rootServices,
            this.workerEvents,
            this.index.AddIdentifier,
            new WorkInitializationExecutor(rootServices));
        var exceptionHandler = new WorkerExecutionExceptionHandler(
            new WorkExceptionClassifierChain(systemExceptionClassifiers, globalExceptionClassifiers, logger),
            logger);
        var attemptRunner = new WorkerExecutionAttemptRunner(invoker, exceptionHandler);
        var completionRecorder = new WorkerExecutionCompletionRecorder(this.workerEvents);
        var runOnce = new RunOnceWorkerExecutionStrategy(
            attemptRunner,
            completionRecorder);
        var transientRetry = new TransientRetryWorkerExecutionStrategy(
            attemptRunner,
            completionRecorder);
        var recurring = new RecurringWorkerExecutionStrategy(
            attemptRunner,
            completionRecorder,
            this.workerEvents);
        this.executionStrategy = new ConfiguredWorkerExecutionStrategy(runOnce, transientRetry, recurring);
        this.concurrency = new WorkConcurrencyCoordinator();
        this.dispatcher = new WorkerDispatcher(this.DispatchQueuedWorker);
        this.retention = new WorkerRetentionScheduler(this.workers, this.Purge, this.PublishPurgeEvent);
    }

    internal async Task<IWorkerHandle> CreateWorker(
        RegisteredWork registeredWork,
        WorkInput? input,
        WorkerOptions? options,
        WorkOrigin origin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!this.acceptingWork)
        {
            return WorkerHandle.Rejected(WorkQueueOutcome.Invalid(
                registeredWork.Definition.Id,
                [WorkMessage.Warning(
                    "workable.system.stopping",
                    "Workable is stopping and is not accepting new work.",
                    "system")]));
        }

        var workerId = WorkerId.New();
        var mergedOptions = registeredWork.Definition.DefaultOptions.Merge(options);
        var configuration = registeredWork.Definition.Configuration
            .Merge(registeredWork.Definition.DefaultOptions.Configuration)
            .Merge(options?.Configuration);
        var configurationErrors = WorkConfigurationValidator.Validate(configuration);
        if (configurationErrors.Count > 0)
        {
            return WorkerHandle.Rejected(WorkQueueOutcome.Invalid(registeredWork.Definition.Id, configurationErrors));
        }

        var concurrencyInputErrors = WorkConfigurationValidator.ValidateConcurrencyInput(
            concurrency: configuration.Concurrency,
            input: input);
        if (concurrencyInputErrors.Count > 0)
        {
            return WorkerHandle.Rejected(WorkQueueOutcome.Invalid(registeredWork.Definition.Id, concurrencyInputErrors));
        }

        var startPolicy = configuration.Start.Policy;
        var shouldStart = startPolicy != WorkStartPolicy.DoNotStart;
        var now = DateTimeOffset.UtcNow;
        WorkerRecord record;
        WorkQueueOutcome outcome;
        bool shouldScheduleStart = false;
        bool shouldDrainQueuedWorkers = false;

        lock (this.subjectSync)
        {
            var idempotencyErrors = this.ValidateIdempotencyLocked(registeredWork.Definition.Id, input?.SubjectId, configuration.Idempotency);
            if (idempotencyErrors.Count > 0)
            {
                return WorkerHandle.Rejected(WorkQueueOutcome.Invalid(
                    registeredWork.Definition.Id,
                    idempotencyErrors));
            }

            if (configuration.Concurrency.IsEnabled && shouldStart)
            {
                var reservation = this.concurrency.QueueWorker(
                    registeredWork.Definition.Id,
                    input,
                    configuration.Concurrency,
                    status =>
                    {
                        var queued = new WorkerRecord(
                            workerId,
                            registeredWork,
                            input,
                            mergedOptions,
                            configuration,
                            origin,
                            WorkerState.Queued,
                            isStartDeferred: status == WorkConcurrencyReservationStatus.Deferred,
                            messages: [],
                            createdAt: now,
                            updatedAt: now);

                        this.workers.TryAdd(workerId, queued);
                        this.index.Register(queued);
                        return queued;
                    });
                if (reservation.Status == WorkConcurrencyReservationStatus.Rejected)
                {
                    return WorkerHandle.Rejected(WorkQueueOutcome.Invalid(
                        registeredWork.Definition.Id,
                        [WorkMessage.Info("workable.concurrency.capacity_reached", "Concurrency capacity has been reached for this work group.", "configuration.concurrency.maximumCapacity")]));
                }

                record = reservation.Worker ?? throw new InvalidOperationException("Accepted concurrency queue reservation did not include a worker.");
                shouldScheduleStart = reservation.Status == WorkConcurrencyReservationStatus.Reserved;
                outcome = WorkQueueOutcome.Accepted(
                    registeredWork.Definition.Id,
                    workerId,
                    reservation.Status == WorkConcurrencyReservationStatus.Deferred
                        ? [WorkMessage.Info("workable.concurrency.start_deferred", "Worker start was deferred until concurrency capacity is available.", "configuration.concurrency")]
                        : null);
            }
            else
            {
                record = new WorkerRecord(
                    workerId,
                    registeredWork,
                    input,
                    mergedOptions,
                    configuration,
                    origin,
                    WorkerState.Queued,
                    isStartDeferred: false,
                    messages: [],
                    createdAt: now,
                    updatedAt: now);

                this.workers.TryAdd(workerId, record);
                this.index.Register(record);
                shouldScheduleStart = shouldStart;
                shouldDrainQueuedWorkers = configuration.Concurrency.IsEnabled && shouldStart;
                outcome = WorkQueueOutcome.Accepted(registeredWork.Definition.Id, workerId);
            }
        }

        this.workerEvents.Queued(record);
        var handle = new WorkerHandle(outcome, record);

        if (shouldScheduleStart)
        {
            this.ScheduleStart(record);
        }

        if (shouldDrainQueuedWorkers)
        {
            this.ScheduleConcurrencyDrain(registeredWork.Definition.Id);
        }

        await WaitForStartPolicy(record, startPolicy, cancellationToken);
        return handle;
    }

    internal void StartDispatching()
    {
        lock (this.lifecycleSync)
        {
            if (this.systemExecutionLifetime.IsCancellationRequested)
            {
                this.retiredSystemExecutionLifetimes.Add(this.systemExecutionLifetime);
                this.systemExecutionLifetime = new CancellationTokenSource();
            }

            this.acceptingWork = true;
        }

        this.dispatcher.Start(this.GetSystemExecutionLifetimeToken());
        this.retention.Start();
    }

    internal async Task StopDispatching(CancellationToken cancellationToken)
    {
        lock (this.lifecycleSync)
        {
            this.acceptingWork = false;
        }

        await this.dispatcher.Stop(cancellationToken);
        var canceledWorkers = this.CancelActive();
        await this.WaitForCanceledWorkers(canceledWorkers, cancellationToken);
        this.ForceCancelRemaining(canceledWorkers);
        lock (this.lifecycleSync)
        {
            this.systemExecutionLifetime.Cancel();
        }

        await this.retention.Stop(cancellationToken);
    }

    public Task<WorkerSnapshot?> Get(WorkerId workerId, CancellationToken cancellationToken = default)
        => Task.FromResult(this.workers.TryGetValue(workerId, out var worker) ? worker.ToSnapshot() : null);

    public Task<IReadOnlyList<WorkerSnapshot>> List(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkerSnapshot>>([.. this.workers.Values.Select(worker => worker.ToSnapshot())]);

    public Task<IReadOnlyList<WorkerSnapshot>> List(WorkSubjectId subjectId, CancellationToken cancellationToken = default)
    {
        lock (this.subjectSync)
        {
            return Task.FromResult<IReadOnlyList<WorkerSnapshot>>(this.GetSubjectWorkersLocked(subjectId));
        }
    }

    public Task<IReadOnlyList<WorkerSnapshot>> List(
        WorkDefinitionId definitionId,
        WorkSubjectId subjectId,
        CancellationToken cancellationToken = default)
    {
        lock (this.subjectSync)
        {
            return Task.FromResult<IReadOnlyList<WorkerSnapshot>>(this.GetSubjectWorkersLocked(definitionId, subjectId));
        }
    }

    public Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default)
        => this.Execute(
            worker,
            action,
            this.dotNetOriginProvider.CreateOrigin($"Apply worker action '{action}' through .NET."),
            cancellationToken);

    internal Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (!this.workers.TryGetValue(worker.WorkerId, out var record))
        {
            return Task.FromResult(WorkActionOutcome.NotFound(action, worker.WorkerId));
        }

        var outcome = action switch
        {
            WorkAction.Start => this.Start(record, worker.Revision, advancesRevision: true, bypassConcurrencyWhenFlexible: true),
            WorkAction.Pause => record.RequestPause(worker.Revision),
            WorkAction.Cancel => record.RequestCancel(worker.Revision),
            WorkAction.Push => record.Push(worker.Revision),
            WorkAction.Purge => this.Purge(record, worker.Revision),
            _ => WorkActionOutcome.Invalid(action, record.ToSnapshot(), [WorkMessage.Error("workable.action.invalid", $"Action '{action}' is not supported.")]),
        };

        record.RecordActionHistory(outcome, origin);

        if (outcome.IsAccepted)
        {
            this.HandleAcceptedWorkerChange(record, action);
        }

        this.workerEvents.ActionApplied(record, outcome, origin);
        return Task.FromResult(outcome);
    }

    public Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default)
        => this.Reconfigure(
            worker,
            changes,
            this.dotNetOriginProvider.CreateOrigin("Reconfigure worker through .NET."),
            cancellationToken);

    internal Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (!this.workers.TryGetValue(worker.WorkerId, out var record))
        {
            return Task.FromResult(WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId));
        }

        var outcome = record.Reconfigure(changes, worker.Revision);
        record.RecordReconfigurationHistory(changes, outcome, origin);
        if (outcome.IsAccepted)
        {
            this.concurrency.Synchronize(record);

            if (record.ShouldStartWithoutConcurrency())
            {
                this.ScheduleStart(record);
            }
            else if (record.ShouldStartWithConcurrency())
            {
                var reservation = this.concurrency.QueueExistingWorkerForStart(record);
                if (reservation == WorkConcurrencyReservationStatus.Reserved)
                {
                    this.ScheduleStart(record);
                }
            }

            this.ScheduleConcurrencyDrain(record.Work.Definition.Id);
        }

        if (outcome.IsAccepted)
        {
            this.workerEvents.Reconfigured(record, changes, outcome, origin);
        }

        return Task.FromResult(outcome);
    }

    public Task<WorkerSnapshot?> GetWorker(WorkerId workerId, CancellationToken cancellationToken = default)
        => this.Get(workerId, cancellationToken);

    public Task<WorkerQueryResult> QueryWorkers(WorkerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = this.GetCandidateWorkers(query);
        var filtered = candidates
            .Where(worker => Matches(worker, query))
            .Select(worker => worker.ToSummary());

        filtered = Sort(filtered, query.Sort, query.Direction);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = query.Take <= 0 ? 100 : query.Take;
        var materialized = filtered.ToList();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerQueryResult(page, materialized.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkInfo?> GetWorkInfo(WorkDefinitionId definitionId, CancellationToken cancellationToken = default)
    {
        if (!this.catalog.TryGet(definitionId, out var definition))
        {
            return Task.FromResult<WorkInfo?>(null);
        }

        return Task.FromResult<WorkInfo?>(this.CreateWorkInfo(definition));
    }

    public Task<WorkInfo?> GetWorkInfo(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!this.catalog.TryGet(name, out var definition))
        {
            return Task.FromResult<WorkInfo?>(null);
        }

        return Task.FromResult<WorkInfo?>(this.CreateWorkInfo(definition));
    }

    public Task<IReadOnlyList<WorkDefinition>> QueryWorkDefinitions(
        WorkDefinitionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = string.IsNullOrWhiteSpace(query.Category)
            ? this.catalog.Definitions
            : this.catalog.ListByCategory(query.Category, query.IncludeSubcategories);
        var definitions = candidates.Where(definition => Matches(definition, query));
        return Task.FromResult<IReadOnlyList<WorkDefinition>>([.. definitions.OrderBy(definition => definition.Category).ThenBy(definition => definition.Name)]);
    }

    public Task<WorkerStatusSummary> GetWorkerStatusSummary(
        WorkerQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new WorkerQuery(Take: int.MaxValue);
        var summaries = this.GetCandidateWorkers(query)
            .Where(worker => Matches(worker, query))
            .Select(worker => worker.ToSummary())
            .ToList();
        var counts = summaries
            .GroupBy(worker => worker.State)
            .ToDictionary(group => group.Key, group => group.Count());
        var final = summaries.Count(worker => WorkerStateMachine.IsFinal(worker.State));
        return Task.FromResult(new WorkerStatusSummary(
            summaries.Count,
            summaries.Count - final,
            final,
            counts));
    }

    private IEnumerable<WorkerRecord> GetCandidateWorkers(WorkerQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.DefinitionName) &&
            this.catalog.TryGet(query.DefinitionName, out var definition))
        {
            query = query with
            {
                DefinitionId = definition.Id,
            };
        }

        var candidateIds = this.index.FindBestCandidates(query);
        return candidateIds is null
            ? this.workers.Values
            : candidateIds.Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker : null)
                .OfType<WorkerRecord>();
    }

    private WorkInfo CreateWorkInfo(WorkDefinition definition)
    {
        var summaries = this.index.ByDefinition(definition.Id)
            .Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker.ToSummary() : null)
            .OfType<WorkerSummary>()
            .ToList();
        var rollup = CreateRollup(summaries);
        return new WorkInfo(definition, StatusFor(rollup), rollup);
    }

    private static WorkerRollup CreateRollup(IReadOnlyList<WorkerSummary> summaries)
    {
        var completed = summaries.Count(worker => worker.State == WorkerState.Completed);
        var canceled = summaries.Count(worker => worker.State == WorkerState.Canceled);
        return new WorkerRollup(
            summaries.Count,
            summaries.Count(worker => !WorkerStateMachine.IsFinal(worker.State)),
            summaries.Count(worker => worker.State == WorkerState.Queued),
            summaries.Count(worker => worker.State is WorkerState.Running or WorkerState.Pausing or WorkerState.Canceling),
            summaries.Count(worker => worker.State == WorkerState.Waiting),
            summaries.Count(worker => worker.State == WorkerState.Paused),
            summaries.Count(worker => worker.State == WorkerState.Failed),
            canceled,
            completed,
            summaries.Count == 0 ? null : summaries.Max(worker => worker.UpdatedAt));
    }

    private static WorkDefinitionStatus StatusFor(WorkerRollup rollup)
    {
        if (rollup.Total == 0 || rollup.Total == rollup.Completed + rollup.Canceled)
        {
            return WorkDefinitionStatus.Inactive;
        }

        if (rollup.Failed > 0 && rollup.Active == rollup.Failed)
        {
            return WorkDefinitionStatus.Critical;
        }

        if (rollup.Failed > 0 || rollup.Paused > 0)
        {
            return WorkDefinitionStatus.NeedsAttention;
        }

        return rollup.Active > 0 ? WorkDefinitionStatus.Healthy : WorkDefinitionStatus.Unknown;
    }

    private static bool Matches(WorkerRecord worker, WorkerQuery query)
    {
        var summary = worker.ToSummary();
        return (query.DefinitionId is null || summary.DefinitionId == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(summary.DefinitionName, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (query.SubjectId is null || summary.SubjectId == query.SubjectId) &&
            (query.ConcurrencyKey is null || summary.ConcurrencyKey == query.ConcurrencyKey) &&
            (query.Identifier is null || summary.Identifiers.Contains(query.Identifier.Value)) &&
            (query.States is null || query.States.Contains(summary.State)) &&
            (query.CreatedFrom is null || summary.CreatedAt >= query.CreatedFrom) &&
            (query.CreatedTo is null || summary.CreatedAt <= query.CreatedTo) &&
            (query.UpdatedFrom is null || summary.UpdatedAt >= query.UpdatedFrom) &&
            (query.UpdatedTo is null || summary.UpdatedAt <= query.UpdatedTo);
    }

    private static bool Matches(WorkDefinition definition, WorkDefinitionQuery query)
        => (query.Id is null || definition.Id == query.Id) &&
            (string.IsNullOrWhiteSpace(query.Name) || string.Equals(definition.Name, query.Name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(definition.Category, query.Category, query.IncludeSubcategories)) &&
            (string.IsNullOrWhiteSpace(query.Search) ||
                definition.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                (definition.Description?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool CategoryMatches(string actual, string expected, bool includeSubcategories)
        => includeSubcategories
            ? actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                actual.StartsWith($"{expected}:", StringComparison.OrdinalIgnoreCase)
            : actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<WorkerSummary> Sort(
        IEnumerable<WorkerSummary> workers,
        WorkerQuerySort sort,
        WorkQuerySortDirection direction)
    {
        var ascending = direction == WorkQuerySortDirection.Ascending;
        return sort switch
        {
            WorkerQuerySort.UpdatedAt => ascending ? workers.OrderBy(worker => worker.UpdatedAt) : workers.OrderByDescending(worker => worker.UpdatedAt),
            WorkerQuerySort.DefinitionName => ascending ? workers.OrderBy(worker => worker.DefinitionName) : workers.OrderByDescending(worker => worker.DefinitionName),
            WorkerQuerySort.State => ascending ? workers.OrderBy(worker => worker.State) : workers.OrderByDescending(worker => worker.State),
            _ => ascending ? workers.OrderBy(worker => worker.CreatedAt) : workers.OrderByDescending(worker => worker.CreatedAt),
        };
    }

    private IReadOnlyList<WorkerRecord> CancelActive()
    {
        var canceledWorkers = new List<WorkerRecord>();
        foreach (var worker in this.workers.Values.Where(worker => !worker.IsFinal))
        {
            var outcome = worker.RequestCancelForSystemStop();
            if (outcome.IsAccepted)
            {
                canceledWorkers.Add(worker);
                this.HandleAcceptedWorkerChange(worker, WorkAction.Cancel);
                var origin = WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Cancel worker during Workable system stop.");
                worker.RecordActionHistory(outcome, origin);
                this.workerEvents.ActionApplied(worker, outcome, origin);
            }
        }

        return canceledWorkers;
    }

    private async Task WaitForCanceledWorkers(
        IReadOnlyList<WorkerRecord> canceledWorkers,
        CancellationToken cancellationToken)
    {
        var pending = canceledWorkers
            .Where(worker => !worker.IsFinal)
            .Select(worker => worker.WaitForCompletion(CancellationToken.None))
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(this.shutdownGracePeriod, cancellationToken);
        }
        catch (TimeoutException)
        {
            return;
        }
    }

    private void ForceCancelRemaining(IReadOnlyList<WorkerRecord> canceledWorkers)
    {
        foreach (var worker in canceledWorkers.Where(worker => !worker.IsFinal))
        {
            var outcome = worker.ForceCancelForSystemStop();
            if (!outcome.IsAccepted)
            {
                continue;
            }

            this.HandleAcceptedWorkerChange(worker, WorkAction.Cancel);
            var origin = WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Force-cancel worker after Workable system shutdown grace period elapsed.");
            worker.RecordActionHistory(outcome, origin);
            this.workerEvents.ActionApplied(worker, outcome, origin);
            this.workerEvents.CompletionRecorded(worker, WorkCompletionStatus.Canceled);
        }
    }

    public void Dispose()
    {
        lock (this.lifecycleSync)
        {
            this.systemExecutionLifetime.Cancel();
            this.dispatcher.Dispose();
            this.retention.Dispose();
            this.systemExecutionLifetime.Dispose();
            foreach (var retiredLifetime in this.retiredSystemExecutionLifetimes)
            {
                retiredLifetime.Dispose();
            }

            this.retiredSystemExecutionLifetimes.Clear();
        }
    }

    private WorkActionOutcome Start(
        WorkerRecord worker,
        long expectedRevision,
        bool advancesRevision,
        bool bypassConcurrencyWhenFlexible = false)
        => this.Start(worker, expectedRevision, advancesRevision, this.GetSystemExecutionLifetimeToken(), bypassConcurrencyWhenFlexible);

    private WorkActionOutcome Start(
        WorkerRecord worker,
        long expectedRevision,
        bool advancesRevision,
        CancellationToken systemExecutionCancellationToken,
        bool bypassConcurrencyWhenFlexible = false)
    {
        var outcome = worker.Configuration.Concurrency.IsEnabled
            ? this.concurrency.TryStart(
                worker,
                expectedRevision,
                advancesRevision,
                systemExecutionCancellationToken,
                bypassConcurrencyWhenFlexible,
                out var executionToken)
            : worker.Start(systemExecutionCancellationToken, expectedRevision, advancesRevision, out executionToken);
        if (!outcome.IsAccepted)
        {
            return outcome;
        }

        worker.TrackCompletion(this.LaunchWorkerExecution(worker, executionToken));
        this.workerEvents.Started(worker);
        return outcome;
    }

    private void ScheduleStart(WorkerRecord worker)
        => this.dispatcher.Schedule(worker);

    private static Task WaitForStartPolicy(
        WorkerRecord worker,
        WorkStartPolicy startPolicy,
        CancellationToken cancellationToken)
        => startPolicy switch
        {
            WorkStartPolicy.StartAndReturnAfterStarted => worker.WaitForStarted(cancellationToken),
            WorkStartPolicy.StartAndReturnAfterCompleted => worker.WaitForCompletion(cancellationToken),
            _ => Task.CompletedTask,
        };

    private Task DispatchQueuedWorker(WorkerRecord worker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Start(worker, worker.Revision, advancesRevision: false, cancellationToken);
        return Task.CompletedTask;
    }

    private CancellationToken GetSystemExecutionLifetimeToken()
    {
        lock (this.lifecycleSync)
        {
            return this.systemExecutionLifetime.Token;
        }
    }

    private Task<WorkCompletion> LaunchWorkerExecution(WorkerRecord worker, CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => this.ExecuteWorkerAndSchedulePurge(worker, cancellationToken), CancellationToken.None);
        }
    }

    private async Task<WorkCompletion> ExecuteWorkerAndSchedulePurge(WorkerRecord worker, CancellationToken cancellationToken)
    {
        var completion = await this.executionStrategy.Execute(worker, cancellationToken);
        this.HandleWorkerExecutionCompleted(worker);
        return completion;
    }

    private void HandleAcceptedWorkerChange(WorkerRecord worker, WorkAction action)
    {
        if (action != WorkAction.Purge && worker.IsFinal)
        {
            this.retention.Schedule(worker);
        }

        this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
    }

    private void HandleWorkerExecutionCompleted(WorkerRecord worker)
    {
        if (worker.IsFinal)
        {
            this.retention.Schedule(worker);
        }

        this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
    }

    private void PublishPurgeEvent(WorkerRecord worker)
    {
        this.workerEvents.Purged(worker);
    }

    private WorkActionOutcome Purge(WorkerRecord worker, long expectedRevision)
    {
        var outcome = worker.Purge(expectedRevision);
        if (!outcome.IsAccepted)
        {
            return outcome;
        }

        this.workers.TryRemove(worker.Id, out _);
        this.concurrency.Forget(worker);
        this.index.Forget(worker);
        this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
        return outcome;
    }

    private IReadOnlyList<WorkMessage> ValidateIdempotencyLocked(
        WorkDefinitionId definitionId,
        WorkSubjectId? subjectId,
        WorkIdempotencyConfiguration idempotency)
    {
        if (!idempotency.IsEnabled)
        {
            return [];
        }

        if (subjectId is not { } requiredSubjectId)
        {
            return [WorkMessage.Error(
                "workable.idempotency.subject_required",
                "Idempotent work requires a work subject id.",
                "input.subjectId")];
        }

        var conflicts = this.GetSubjectWorkersLocked(definitionId, requiredSubjectId)
            .Where(worker => worker.State != WorkerState.Canceled)
            .ToList();

        return conflicts.Count == 0
            ? []
            : [WorkMessage.Error(
                "workable.idempotency.duplicate_subject",
                $"A worker already exists for work subject '{requiredSubjectId}'.",
                "input.subjectId")];
    }

    private IReadOnlyList<WorkerSnapshot> GetSubjectWorkersLocked(WorkSubjectId subjectId)
        => this.GetSnapshotsNewestFirst(this.index.BySubject(subjectId));

    private IReadOnlyList<WorkerSnapshot> GetSubjectWorkersLocked(WorkDefinitionId definitionId, WorkSubjectId subjectId)
        => this.GetSnapshotsNewestFirst(this.index.ByDefinitionAndSubject(definitionId, subjectId));

    private IReadOnlyList<WorkerSnapshot> GetSnapshotsNewestFirst(IEnumerable<WorkerId> workerIds)
        => [.. workerIds
            .Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker.ToSnapshot() : null)
            .OfType<WorkerSnapshot>()
            .OrderByDescending(worker => worker.CreatedAt)];

    private void ScheduleConcurrencyDrain(WorkDefinitionId definitionId)
    {
        foreach (var worker in this.concurrency.ReserveDeferredStarts(definitionId))
        {
            this.ScheduleStart(worker);
        }
    }

    private sealed class WorkerRetentionScheduler(
        ConcurrentDictionary<WorkerId, WorkerRecord> workers,
        Func<WorkerRecord, long, WorkActionOutcome> purge,
        Action<WorkerRecord> publishPurgeEvent) : IDisposable
    {
        private readonly PriorityQueue<ScheduledPurge, DateTimeOffset> scheduledPurges = new();
        private readonly SemaphoreSlim signal = new(0);
        private readonly Lock sync = new();
        private CancellationTokenSource? cancellation;
        private Task? schedulerTask;

        public void Start()
        {
            lock (this.sync)
            {
                if (this.schedulerTask is { IsCompleted: false })
                {
                    return;
                }

                this.cancellation?.Dispose();
                this.cancellation = new CancellationTokenSource();
                using (ExecutionContext.SuppressFlow())
                {
                    this.schedulerTask = Task.Run(() => this.Run(this.cancellation.Token), CancellationToken.None);
                }

                this.signal.Release();
            }
        }

        public async Task Stop(CancellationToken cancellationToken)
        {
            Task? task;
            lock (this.sync)
            {
                this.cancellation?.Cancel();
                task = this.schedulerTask;
                this.signal.Release();
            }

            if (task is not null)
            {
                await task.WaitAsync(cancellationToken);
            }
        }

        public void Schedule(WorkerRecord worker)
        {
            if (!worker.IsFinal)
            {
                return;
            }

            var dueAt = DateTimeOffset.UtcNow + worker.Configuration.Retention.PurgeInterval;
            lock (this.sync)
            {
                this.scheduledPurges.Enqueue(new ScheduledPurge(worker.Id), dueAt);
                this.signal.Release();
            }
        }

        public void Dispose()
        {
            lock (this.sync)
            {
                this.cancellation?.Cancel();
                this.signal.Release();
                this.cancellation?.Dispose();
            }
        }

        private async Task Run(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (this.TryTakeDuePurge(out var scheduledPurge))
                    {
                        this.TryPurge(scheduledPurge);
                        continue;
                    }

                    var delay = this.GetDelayUntilNextPurge();
                    if (delay == Timeout.InfiniteTimeSpan)
                    {
                        await this.signal.WaitAsync(cancellationToken);
                    }
                    else
                    {
                        await this.signal.WaitAsync(delay, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        private bool TryTakeDuePurge([NotNullWhen(true)] out ScheduledPurge? scheduledPurge)
        {
            lock (this.sync)
            {
                if (!this.scheduledPurges.TryPeek(out var next, out var dueAt) ||
                    dueAt > DateTimeOffset.UtcNow)
                {
                    scheduledPurge = null;
                    return false;
                }

                scheduledPurge = this.scheduledPurges.Dequeue();
                return true;
            }
        }

        private TimeSpan GetDelayUntilNextPurge()
        {
            lock (this.sync)
            {
                if (!this.scheduledPurges.TryPeek(out _, out var dueAt))
                {
                    return Timeout.InfiniteTimeSpan;
                }

                var delay = dueAt - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }

        private void TryPurge(ScheduledPurge scheduledPurge)
        {
            if (!workers.TryGetValue(scheduledPurge.WorkerId, out var worker) || !worker.IsFinal)
            {
                return;
            }

            var outcome = purge(worker, worker.Revision);
            if (outcome.IsAccepted)
            {
                publishPurgeEvent(worker);
            }
        }

        private sealed record ScheduledPurge(WorkerId WorkerId);
    }

    private sealed class WorkConcurrencyCoordinator
    {
        private readonly ConcurrentDictionary<WorkDefinitionId, WorkDefinitionConcurrencyManager> managers = [];

        public WorkConcurrencyReservation QueueWorker(
            WorkDefinitionId definitionId,
            WorkInput? input,
            WorkConcurrencyConfiguration configuration,
            Func<WorkConcurrencyReservationStatus, WorkerRecord> createWorker)
        {
            var manager = this.GetManager(definitionId);
            return manager.QueueWorker(input, configuration, createWorker);
        }

        public WorkActionOutcome TryStart(
            WorkerRecord worker,
            long expectedRevision,
            bool advancesRevision,
            CancellationToken cancellationToken,
            bool bypassConcurrencyWhenFlexible,
            out CancellationToken executionToken)
        {
            var configuration = worker.Configuration.Concurrency;
            var manager = this.GetManager(worker.Work.Definition.Id);
            return manager.TryStart(
                worker,
                expectedRevision,
                advancesRevision,
                cancellationToken,
                bypassConcurrencyWhenFlexible,
                configuration,
                out executionToken);
        }

        public WorkConcurrencyReservationStatus QueueExistingWorkerForStart(WorkerRecord worker)
        {
            var configuration = worker.Configuration.Concurrency;
            var manager = this.GetManager(worker.Work.Definition.Id);
            return manager.QueueExistingWorkerForStart(worker, configuration);
        }

        public IReadOnlyList<WorkerRecord> ReserveDeferredStarts(WorkDefinitionId definitionId)
        {
            return this.managers.TryGetValue(definitionId, out var manager)
                ? manager.ReserveDeferredStarts()
                : [];
        }

        public void Synchronize(WorkerRecord worker)
        {
            if (worker.Configuration.Concurrency.IsEnabled)
            {
                this.GetManager(worker.Work.Definition.Id).Track(worker);
                return;
            }

            this.Forget(worker);
        }

        public void Forget(WorkerRecord worker)
        {
            if (this.managers.TryGetValue(worker.Work.Definition.Id, out var manager))
            {
                manager.Forget(worker);
            }
        }

        private WorkDefinitionConcurrencyManager GetManager(WorkDefinitionId definitionId)
            => this.managers.GetOrAdd(definitionId, static id => new WorkDefinitionConcurrencyManager(id));

        private sealed class WorkDefinitionConcurrencyManager(WorkDefinitionId definitionId)
        {
            private readonly Lock sync = new();
            private readonly Dictionary<WorkerId, WorkerRecord> workers = [];
            private readonly Queue<WorkerId> deferredStarts = [];

            public WorkConcurrencyReservation QueueWorker(
                WorkInput? input,
                WorkConcurrencyConfiguration configuration,
                Func<WorkConcurrencyReservationStatus, WorkerRecord> createWorker)
            {
                lock (this.sync)
                {
                    var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, input);
                    var status = this.HasCapacity(configuration, groupKey)
                        ? WorkConcurrencyReservationStatus.Reserved
                        : configuration.LimitReachedBehavior == WorkConcurrencyLimitReachedBehavior.DeferStart
                            ? WorkConcurrencyReservationStatus.Deferred
                            : WorkConcurrencyReservationStatus.Rejected;

                    if (status == WorkConcurrencyReservationStatus.Rejected)
                    {
                        return new WorkConcurrencyReservation(status, Worker: null);
                    }

                    var worker = createWorker(status);
                    this.TrackLocked(worker);
                    if (status == WorkConcurrencyReservationStatus.Deferred)
                    {
                        this.deferredStarts.Enqueue(worker.Id);
                    }

                    return new WorkConcurrencyReservation(status, worker);
                }
            }

            public WorkConcurrencyReservationStatus QueueExistingWorkerForStart(
                WorkerRecord worker,
                WorkConcurrencyConfiguration configuration)
            {
                lock (this.sync)
                {
                    this.TrackLocked(worker);

                    var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                    var status = this.HasCapacity(configuration, groupKey, worker)
                        ? WorkConcurrencyReservationStatus.Reserved
                        : configuration.LimitReachedBehavior == WorkConcurrencyLimitReachedBehavior.DeferStart
                            ? WorkConcurrencyReservationStatus.Deferred
                            : WorkConcurrencyReservationStatus.Rejected;

                    if (status == WorkConcurrencyReservationStatus.Deferred)
                    {
                        worker.DeferConcurrencyStart();
                        if (!this.deferredStarts.Contains(worker.Id))
                        {
                            this.deferredStarts.Enqueue(worker.Id);
                        }
                    }

                    return status;
                }
            }

            public WorkActionOutcome TryStart(
                WorkerRecord worker,
                long expectedRevision,
                bool advancesRevision,
                CancellationToken cancellationToken,
                bool bypassConcurrencyWhenFlexible,
                WorkConcurrencyConfiguration configuration,
                out CancellationToken executionToken)
            {
                lock (this.sync)
                {
                    if (!bypassConcurrencyWhenFlexible ||
                        configuration.OverrideBehavior == WorkConcurrencyOverrideBehavior.Strict)
                    {
                        var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                        if (!this.HasCapacity(configuration, groupKey, worker))
                        {
                            executionToken = default;
                            return WorkActionOutcome.Invalid(
                                WorkAction.Start,
                                worker.ToSnapshot(),
                                [WorkMessage.Info("workable.concurrency.capacity_reached", "Concurrency capacity has been reached for this work group.", "configuration.concurrency.maximumCapacity")]);
                        }
                    }

                    var outcome = worker.Start(cancellationToken, expectedRevision, advancesRevision, out executionToken);
                    if (outcome.IsAccepted)
                    {
                        this.TrackLocked(worker);
                        this.RemoveDeferred(worker.Id);
                    }

                    return outcome;
                }
            }

            public IReadOnlyList<WorkerRecord> ReserveDeferredStarts()
            {
                lock (this.sync)
                {
                    var scheduled = new List<WorkerRecord>();
                    var retained = new Queue<WorkerId>(this.deferredStarts.Count);
                    while (this.deferredStarts.Count > 0)
                    {
                        var workerId = this.deferredStarts.Dequeue();
                        if (!this.workers.TryGetValue(workerId, out var worker) ||
                            !worker.IsDeferredConcurrencyStartFor(definitionId))
                        {
                            continue;
                        }

                        var configuration = worker.Configuration.Concurrency;
                        var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                        if (!this.HasCapacity(configuration, groupKey))
                        {
                            retained.Enqueue(workerId);
                            continue;
                        }

                        worker.ReserveDeferredConcurrencyStart();
                        scheduled.Add(worker);
                    }

                    while (retained.TryDequeue(out var workerId))
                    {
                        this.deferredStarts.Enqueue(workerId);
                    }

                    return scheduled;
                }
            }

            public void Track(WorkerRecord worker)
            {
                lock (this.sync)
                {
                    this.TrackLocked(worker);
                }
            }

            public void Forget(WorkerRecord worker)
            {
                lock (this.sync)
                {
                    this.workers.Remove(worker.Id);
                    this.RemoveDeferred(worker.Id);
                }
            }

            private bool HasCapacity(
                WorkConcurrencyConfiguration configuration,
                WorkConcurrencyGroupKey groupKey,
                WorkerRecord? candidate = null)
            {
                var count = 0;
                var candidateAlreadyCounts = false;

                foreach (var worker in this.workers.Values)
                {
                    if (!worker.CountsAgainstConcurrencyCapacity(configuration.BlockingMode))
                    {
                        continue;
                    }

                    if (WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input) != groupKey)
                    {
                        continue;
                    }

                    count++;
                    if (ReferenceEquals(worker, candidate))
                    {
                        candidateAlreadyCounts = true;
                    }
                }

                return count < configuration.MaximumCapacity ||
                    candidateAlreadyCounts && count == configuration.MaximumCapacity;
            }

            private void TrackLocked(WorkerRecord worker)
            {
                this.workers[worker.Id] = worker;
            }

            private void RemoveDeferred(WorkerId workerId)
            {
                if (!this.deferredStarts.Contains(workerId))
                {
                    return;
                }

                var retained = new Queue<WorkerId>(this.deferredStarts.Count);
                while (this.deferredStarts.TryDequeue(out var current))
                {
                    if (current != workerId)
                    {
                        retained.Enqueue(current);
                    }
                }

                while (retained.TryDequeue(out var current))
                {
                    this.deferredStarts.Enqueue(current);
                }
            }
        }

        private readonly record struct WorkConcurrencyGroupKey(
            WorkConcurrencyScope Scope,
            WorkSubjectId? SubjectId,
            WorkConcurrencyKey? ConcurrencyKey)
        {
            public static WorkConcurrencyGroupKey From(WorkConcurrencyScope scope, WorkInput? input)
                => scope switch
                {
                    WorkConcurrencyScope.PerSubject => new(scope, input?.SubjectId, null),
                    WorkConcurrencyScope.PerConcurrencyKey => new(scope, null, input?.ConcurrencyKey),
                    _ => new(WorkConcurrencyScope.PerDefinition, null, null),
                };
        }
    }

    private readonly record struct WorkConcurrencyReservation(
        WorkConcurrencyReservationStatus Status,
        WorkerRecord? Worker);

    private enum WorkConcurrencyReservationStatus
    {
        Reserved,
        Deferred,
        Rejected,
    }

    private sealed class WorkerDispatcher(Func<WorkerRecord, CancellationToken, Task> dispatch) : IDisposable
    {
        private readonly Channel<WorkerRecord> scheduledWorkers = Channel.CreateUnbounded<WorkerRecord>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private readonly Lock sync = new();
        private CancellationTokenSource? cancellation;
        private Task? dispatchTask;

        public void Start(CancellationToken cancellationToken)
        {
            lock (this.sync)
            {
                if (this.dispatchTask is { IsCompleted: false })
                {
                    return;
                }

                this.cancellation?.Dispose();
                this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using (ExecutionContext.SuppressFlow())
                {
                    this.dispatchTask = Task.Run(() => this.Run(this.cancellation.Token), CancellationToken.None);
                }
            }
        }

        public void Schedule(WorkerRecord worker)
            => this.scheduledWorkers.Writer.TryWrite(worker);

        public async Task Stop(CancellationToken cancellationToken)
        {
            Task? task;
            lock (this.sync)
            {
                this.cancellation?.Cancel();
                task = this.dispatchTask;
            }

            if (task is null)
            {
                return;
            }

            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            lock (this.sync)
            {
                this.cancellation?.Cancel();
                this.cancellation?.Dispose();
                this.cancellation = null;
            }
        }

        private async Task Run(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var worker in this.scheduledWorkers.Reader.ReadAllAsync(cancellationToken))
                {
                    await dispatch(worker, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}

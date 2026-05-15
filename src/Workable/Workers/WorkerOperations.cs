using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkerOperations : IWorkerOperations, IWorkQuery, IDisposable
{
    private const int OverviewWorkerListSize = 5;
    private const int OverviewIterationListSize = 5;
    private const int OverviewCommonKeyTypeCount = 10;

    private readonly WorkSystemCatalog catalog;
    private readonly Func<WorkSystemState> getSystemState;
    private readonly string? workSystemName;
    private readonly WorkerEventPublisher workerEvents;
    private readonly IWorkerExecutionStrategy executionStrategy;
    private readonly ConcurrentDictionary<WorkerId, WorkerRecord> workers = [];
    private readonly WorkerIndex index = new();
    private readonly WorkerIterationIndex iterationIndex = new(OverviewIterationListSize);
    private readonly InMemoryWorkMetricsSink metrics;
    private readonly Lock lifecycleSync = new();
    private readonly Lock subjectSync = new();
    private readonly List<CancellationTokenSource> retiredSystemExecutionLifetimes = [];
    private readonly WorkerDispatcher dispatcher;
    private readonly WorkConcurrencyCoordinator concurrency;
    private readonly WorkerRetentionScheduler retention;
    private readonly IDotNetWorkOriginProvider dotNetOriginProvider;
    private readonly TimeSpan shutdownGracePeriod;
    private CancellationTokenSource systemExecutionLifetime = new();
    private volatile bool acceptingWork;

    internal WorkerOperations(
        WorkSystemCatalog catalog,
        Func<WorkSystemState> getSystemState,
        WorkSystemId workSystemId,
        string? workSystemName,
        IServiceProvider rootServices,
        WorkEventStream events,
        IDotNetWorkOriginProvider dotNetOriginProvider,
        IReadOnlyList<WorkExceptionClassifier> systemExceptionClassifiers,
        IReadOnlyList<WorkExceptionClassifier> globalExceptionClassifiers,
        TimeSpan shutdownGracePeriod,
        InMemoryWorkMetricsSink metrics)
    {
        this.catalog = catalog;
        this.getSystemState = getSystemState;
        this.workSystemName = workSystemName;
        this.dotNetOriginProvider = dotNetOriginProvider;
        this.shutdownGracePeriod = shutdownGracePeriod;
        this.metrics = metrics;
        this.workerEvents = new WorkerEventPublisher(workSystemId, events, this.SynchronizeWorkerIfTracked);
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
            completionRecorder,
            this.workerEvents);
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

        if (!this.TryAcceptWork(registeredWork.Definition.Id, out var rejection))
        {
            return WorkerHandle.Rejected(rejection);
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

                        this.AttachIndexCallbacks(queued);
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

                this.AttachIndexCallbacks(record);
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

    private bool TryAcceptWork(
        WorkDefinitionId definitionId,
        [NotNullWhen(false)] out WorkQueueOutcome? rejection)
    {
        lock (this.lifecycleSync)
        {
            var systemState = this.getSystemState();
            if (systemState is WorkSystemState.Stopping)
            {
                rejection = WorkQueueOutcome.Invalid(
                    definitionId,
                    [WorkMessage.Warning(
                        "workable.system.stopping",
                        "Workable is stopping and is not accepting new work.",
                        "system")]);
                return false;
            }

            if (systemState is not WorkSystemState.Started)
            {
                rejection = WorkQueueOutcome.Invalid(
                    definitionId,
                    [WorkMessage.Warning(
                        "workable.system.not_started",
                        $"Workable is '{systemState}' and is not accepting new work.",
                        "system")]);
                return false;
            }

            if (!this.acceptingWork)
            {
                rejection = WorkQueueOutcome.Invalid(
                    definitionId,
                    [WorkMessage.Warning(
                        "workable.system.stopping",
                        "Workable is stopping and is not accepting new work.",
                        "system")]);
                return false;
            }
        }

        rejection = null;
        return true;
    }

    private void RegisterIterationIfTracked(WorkerRecord worker, WorkerIterationSnapshot iteration)
    {
        if (this.workers.ContainsKey(worker.Id))
        {
            var existing = this.iterationIndex.Get(new WorkerIterationReference(worker.Id, iteration.Sequence));
            this.iterationIndex.Register(worker, iteration);
            if (existing is null || existing.Status != iteration.Status)
            {
                this.metrics.IterationRecorded(worker.Work.Definition.Id, iteration);
            }
        }
    }

    private void ForgetIterationIfTracked(WorkerRecord worker, WorkerIterationReference iteration)
    {
        if (this.workers.ContainsKey(worker.Id))
        {
            this.iterationIndex.Forget(iteration);
        }
    }

    private void AttachIndexCallbacks(WorkerRecord worker)
    {
        worker.IterationRecorded = this.RegisterIterationIfTracked;
        worker.IterationForgotten = this.ForgetIterationIfTracked;
    }

    private void SynchronizeWorkerIfTracked(WorkerRecord worker)
    {
        if (this.workers.ContainsKey(worker.Id))
        {
            this.index.Synchronize(worker);
        }
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

    internal Task<WorkSystemStopResult> StopDispatching(CancellationToken cancellationToken)
        => this.StopDispatching(
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Stop Workable system through .NET."),
            cancellationToken);

    internal async Task<WorkSystemStopResult> StopDispatching(
        WorkOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);

        lock (this.lifecycleSync)
        {
            this.acceptingWork = false;
        }

        await this.dispatcher.Stop(cancellationToken);
        await this.retention.Stop(cancellationToken);
        var canceledWorkers = this.CancelActive(origin);
        await this.WaitForCanceledWorkers(canceledWorkers, cancellationToken);
        var forceCanceledWorkers = this.ForceCancelRemaining(canceledWorkers, origin);
        lock (this.lifecycleSync)
        {
            this.systemExecutionLifetime.Cancel();
        }

        this.ClearWorkerMemory();
        return new WorkSystemStopResult(forceCanceledWorkers)
        {
            CancellationRequestedWorkers = [.. canceledWorkers.Select(worker => worker.ToSnapshot())],
            CancellationRequestedWorkerSummaries = [.. canceledWorkers
                .Select(worker => WorkSystemShutdownWorker.From(worker.ToSnapshot()))],
            ForceCanceledWorkerSummaries = [.. forceCanceledWorkers
                .Select(WorkSystemShutdownWorker.From)],
            ShutdownGracePeriod = this.shutdownGracePeriod,
        };
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

        return Task.FromResult(this.ApplyAction(worker, action, origin));
    }

    public Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
        => this.ExecuteAll(
            action,
            filter,
            this.dotNetOriginProvider.CreateOrigin($"Apply worker action '{action}' to multiple workers through .NET."),
            cancellationToken);

    internal Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);

        filter ??= WorkerBulkActionFilter.All;
        var candidates = this.GetBulkActionCandidates(filter);
        var outcomes = new List<WorkActionOutcome>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = candidate.ToSummary().Version;
            outcomes.Add(this.ApplyAction(version, action, origin));
        }

        return Task.FromResult(new WorkerBulkActionOutcome(
            action,
            filter,
            candidates.Count,
            outcomes));
    }

    private WorkActionOutcome ApplyAction(
        WorkerVersion worker,
        WorkAction action,
        WorkOrigin origin)
    {
        if (!this.workers.TryGetValue(worker.WorkerId, out var record))
        {
            return WorkActionOutcome.NotFound(action, worker.WorkerId);
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
            if (action != WorkAction.Purge)
            {
                this.SynchronizeWorkerIfTracked(record);
                record.SignalCurrentCompletion();
            }
        }

        this.workerEvents.ActionApplied(record, outcome, origin);
        return outcome;
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

    public Task<WorkerIterationSnapshot?> GetWorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(this.iterationIndex.Get(iteration));

    public Task<WorkerQueryResult> QueryWorkers(WorkerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = this.GetCandidateWorkers(query);
        var filtered = candidates
            .Select(worker => new
            {
                Record = worker,
                Overview = worker.ToOverviewItem(),
            })
            .Where(worker => Matches(worker.Overview, query) && Matches(worker.Record, query.Configuration))
            .Select(worker => worker.Overview);

        filtered = Sort(filtered, query.Sort, query.Direction);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = query.Take <= 0
            ? WorkerQuery.DefaultTake
            : Math.Min(query.Take, WorkerQuery.MaximumTake);
        var materialized = filtered.ToList();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerQueryResult(page, materialized.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkerIterationQueryResult> QueryWorkerIterations(
        WorkerIterationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var normalizedQuery = query;
        if (!string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            if (!this.catalog.TryGet(query.DefinitionName, out var definition))
            {
                var emptySkip = Math.Max(0, query.Skip);
                var emptyTake = query.Take <= 0
                    ? WorkerIterationQuery.DefaultTake
                    : Math.Min(query.Take, WorkerIterationQuery.MaximumTake);
                return Task.FromResult(new WorkerIterationQueryResult([], 0, emptySkip, emptyTake));
            }

            normalizedQuery = query with
            {
                DefinitionId = definition.Id,
            };
        }

        IReadOnlySet<WorkDefinitionId>? definitionIds = null;
        if (!string.IsNullOrWhiteSpace(normalizedQuery.Category))
        {
            definitionIds = this.catalog
                .ListByCategory(normalizedQuery.Category, includeSubcategories: true)
                .Select(definition => definition.Id)
                .ToHashSet();
        }

        var iterations = this.iterationIndex.Find(normalizedQuery, definitionIds)
            .Where(iteration => Matches(iteration, normalizedQuery))
            .Select(iteration => iteration.ToOverviewItem());

        iterations = Sort(iterations, query.Sort, query.Direction);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = query.Take <= 0
            ? WorkerIterationQuery.DefaultTake
            : Math.Min(query.Take, WorkerIterationQuery.MaximumTake);
        var materialized = iterations.ToList();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerIterationQueryResult(page, materialized.Count, normalizedSkip, normalizedTake));
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

    public Task<WorkerKeyQueryResult> QueryWorkerKeys(
        WorkerKeyQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkKeyTake(query.Take);
        var matches = this.index.WorkKeys(query.Kind, query.Type, query.Value)
            .Where(key => Matches(key, query))
            .OrderBy(key => key.Kind)
            .ThenBy(key => key.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Value, StringComparer.OrdinalIgnoreCase)
            .Select(key => new WorkerKeyDescriptor(
                key.Kind,
                key.Type,
                key.Value,
                this.GetOverviewItems(key.WorkerIds, query.States)))
            .Where(key => key.Workers.Count > 0)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerKeyQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkerKeyTypeQueryResult> QueryWorkerKeyTypes(
        WorkerKeyTypeQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new WorkerKeyTypeQuery();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkKeyTake(query.Take);
        if (query.States is null)
        {
            var facets = this.index.KeyTypes(query.Kind, query.Type, query.Search);
            var keyTypePage = facets
                .Skip(normalizedSkip)
                .Take(normalizedTake)
                .Select(facet => new WorkerKeyTypeDescriptor(
                    facet.Type,
                    facet.WorkerCount,
                    facet.WorkerCountByKind,
                    this.GetOverviewItems(this.index.WorkerIdsByKeyType(facet.Type, query.Kind))))
                .ToArray();

            return Task.FromResult(new WorkerKeyTypeQueryResult(keyTypePage, facets.Count, normalizedSkip, normalizedTake));
        }

        var matches = this.index.WorkKeys(query.Kind, query.Type, null)
            .Where(key => Matches(key, query))
            .GroupBy(key => key.Type.ToUpperInvariant())
            .Select(group =>
            {
                var first = group.First();
                var workers = this.GetOverviewItems(group.SelectMany(key => key.WorkerIds).Distinct(), query.States);
                return new WorkerKeyTypeDescriptor(
                    first.Type,
                    workers.Count,
                    CountWorkersByKind(group, query.States),
                    workers);
            })
            .Where(keyType => keyType.Workers.Count > 0)
            .OrderByDescending(keyType => keyType.WorkerCount)
            .ThenBy(keyType => keyType.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerKeyTypeQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkIterationKeyQueryResult> QueryWorkIterationKeys(
        WorkIterationKeyQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkIterationKeyTake(query.Take);
        var matches = this.iterationIndex.WorkKeys(query.Kind, query.Type, query.Value)
            .Where(key => Matches(key, query))
            .OrderBy(key => key.Kind)
            .ThenBy(key => key.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Value, StringComparer.OrdinalIgnoreCase)
            .Select(key => new WorkIterationKeyDescriptor(
                key.Kind,
                key.Type,
                key.Value,
                this.iterationIndex.GetOverviewItems(key.IterationReferences, query.Statuses)))
            .Where(key => key.Iterations.Count > 0)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkIterationKeyQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkIterationKeyTypeQueryResult> QueryWorkIterationKeyTypes(
        WorkIterationKeyTypeQuery? query = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(this.CreateWorkIterationKeyTypes(query));

    private WorkIterationKeyTypeQueryResult CreateWorkIterationKeyTypes(WorkIterationKeyTypeQuery? query)
    {
        query ??= new WorkIterationKeyTypeQuery();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkIterationKeyTake(query.Take);
        if (query.Statuses is null)
        {
            var facets = this.iterationIndex.KeyTypes(query.Kind, query.Type, query.Search);
            var keyTypePage = facets
                .Skip(normalizedSkip)
                .Take(normalizedTake)
                .Select(facet => new WorkIterationKeyTypeDescriptor(
                    facet.Type,
                    facet.IterationCount,
                    facet.IterationCountByKind,
                    this.iterationIndex.GetOverviewItems(this.iterationIndex.IterationReferencesByKeyType(facet.Type, query.Kind))))
                .ToArray();

            return new WorkIterationKeyTypeQueryResult(keyTypePage, facets.Count, normalizedSkip, normalizedTake);
        }

        var matches = this.iterationIndex.WorkKeys(query.Kind, query.Type, null)
            .Where(key => Matches(key, query))
            .GroupBy(key => key.Type.ToUpperInvariant())
            .Select(group =>
            {
                var first = group.First();
                var iterations = this.iterationIndex.GetOverviewItems(
                    group.SelectMany(key => key.IterationReferences).Distinct(),
                    query.Statuses);
                return new WorkIterationKeyTypeDescriptor(
                    first.Type,
                    iterations.Count,
                    CountIterationsByKind(group, query.Statuses),
                    iterations);
            })
            .Where(keyType => keyType.Iterations.Count > 0)
            .OrderByDescending(keyType => keyType.IterationCount)
            .ThenBy(keyType => keyType.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkIterationKeyTypeQueryResult(page, matches.Count, normalizedSkip, normalizedTake);
    }

    public Task<WorkerStatusSummary> GetWorkerStatusSummary(
        WorkerQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        if (query is null || IsWholeSystemStatusSummary(query))
        {
            return Task.FromResult(CreateStatusSummary(this.index.CountByState()));
        }

        query ??= new WorkerQuery();
        var workers = this.GetCandidateWorkers(query)
            .Select(worker => new
            {
                Record = worker,
                Overview = worker.ToOverviewItem(),
            })
            .Where(worker => Matches(worker.Overview, query) && Matches(worker.Record, query.Configuration))
            .Select(worker => worker.Overview)
            .ToList();
        var counts = workers
            .GroupBy(worker => worker.State)
            .ToDictionary(group => group.Key, group => group.Count());
        var active = workers.Count(worker => IsActiveForSummary(worker.State));
        var final = workers.Count(worker => WorkerStateMachine.IsFinal(worker.State));
        return Task.FromResult(new WorkerStatusSummary(
            workers.Count,
            active,
            final,
            counts));
    }

    private static WorkerStatusSummary CreateStatusSummary(IReadOnlyDictionary<WorkerState, int> counts)
    {
        var total = counts.Values.Sum();
        var final = counts
            .Where(count => WorkerStateMachine.IsFinal(count.Key))
            .Sum(count => count.Value);
        var active = counts
            .Where(count => IsActiveForSummary(count.Key))
            .Sum(count => count.Value);
        return new WorkerStatusSummary(
            total,
            active,
            final,
            counts);
    }

    private static bool IsActiveForSummary(WorkerState state)
        => !WorkerStateMachine.IsFinal(state) && state != WorkerState.Failed;

    private static bool IsWholeSystemStatusSummary(WorkerQuery query)
        => query.DefinitionId is null &&
            string.IsNullOrWhiteSpace(query.DefinitionName) &&
            string.IsNullOrWhiteSpace(query.Category) &&
            query.SubjectId is null &&
            query.ConcurrencyKey is null &&
            query.Identifier is null &&
            query.States is null &&
            query.Configuration is null &&
            query.CreatedFrom is null &&
            query.CreatedTo is null &&
            query.UpdatedFrom is null &&
            query.UpdatedTo is null;

    public Task<WorkSystemOverview> GetSystemOverview(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var definitionIds = this.ResolveDefinitionScope(query);
        var workerCounts = this.CreateOverviewWorkerCounts(definitionIds);
        var iterationCounts = this.CreateOverviewIterationCounts(definitionIds);
        var catalogLevel = this.CreateOverviewCatalogLevel(query);

        return Task.FromResult(new WorkSystemOverview(
            this.workSystemName,
            this.getSystemState(),
            this.index.ActiveOrQueuedDefinitionCount(definitionIds),
            catalogLevel.Categories,
            catalogLevel.Definitions,
            workerCounts.ActiveWorkerCount,
            workerCounts.FinalWorkerCount,
            workerCounts.FailedWorkerCount,
            workerCounts.WorkerCountByState,
            iterationCounts.CurrentIterationCount,
            iterationCounts.CompletedIterationCount,
            iterationCounts.FailedIterationCount,
            iterationCounts.CanceledIterationCount,
            iterationCounts.IterationCountByStatus,
            this.CreateOverviewCommonKeyTypes(definitionIds),
            query?.IncludeThroughput == true ? this.CreateOverviewThroughput(definitionIds) : null,
            this.CreateOverviewFailedWorkers(definitionIds),
            this.CreateOverviewFailedIterations(definitionIds),
            this.CreateOverviewCompletedIterations(definitionIds)));
    }

    public Task<WorkComponentQueryResult> QueryComponents(
        WorkComponentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requests = NormalizeComponentRequests(query?.Components);
        var definitionIds = this.ResolveDefinitionScope(query?.Scope);
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            components[request.Id] = this.CreateComponent(request, query?.Scope, definitionIds);
        }

        return Task.FromResult(new WorkComponentQueryResult(DateTimeOffset.UtcNow, components));
    }

    public Task<WorkComponentQueryResult> GetView(
        string name,
        WorkViewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(name, "overview", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WorkComponentQueryResult(
                DateTimeOffset.UtcNow,
                new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase)
                {
                    [name] = new("error", Error: $"Unknown view '{name}'."),
                }));
        }

        return this.QueryComponents(
            new WorkComponentQuery(
                query?.Scope,
                NormalizeComponentRequests(query?.Components)),
            cancellationToken);
    }

    public Task<WorkSystemThroughput> GetSystemOverviewThroughput(
        WorkOverviewQuery? query = null,
        WorkThroughputQuery? throughputQuery = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateOverviewThroughput(this.ResolveDefinitionScope(query), throughputQuery));
    }

    public Task<WorkSystemOverviewCounts> GetSystemOverviewCounts(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var definitionIds = this.ResolveDefinitionScope(query);
        var workerCounts = this.CreateOverviewWorkerCounts(definitionIds);
        var iterationCounts = this.CreateOverviewIterationCounts(definitionIds);
        return Task.FromResult(new WorkSystemOverviewCounts(
            this.workSystemName,
            this.getSystemState(),
            this.index.ActiveOrQueuedDefinitionCount(definitionIds),
            workerCounts.ActiveWorkerCount,
            workerCounts.FinalWorkerCount,
            workerCounts.FailedWorkerCount,
            iterationCounts.CurrentIterationCount,
            iterationCounts.CompletedIterationCount,
            iterationCounts.FailedIterationCount,
            iterationCounts.CanceledIterationCount));
    }

    public Task<WorkSystemWorkerCounts> GetSystemOverviewWorkerCounts(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateOverviewWorkerCounts(this.ResolveDefinitionScope(query)));
    }

    public Task<WorkSystemIterationCounts> GetSystemOverviewIterationCounts(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateOverviewIterationCounts(this.ResolveDefinitionScope(query)));
    }

    public Task<IReadOnlyList<WorkIterationKeyTypeFacet>> GetSystemOverviewCommonKeyTypes(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkIterationKeyTypeFacet>>(this.CreateOverviewCommonKeyTypes(this.ResolveDefinitionScope(query)));
    }

    public Task<WorkSystemFailedWorkersOverview> GetSystemOverviewFailedWorkers(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definitionIds = this.ResolveDefinitionScope(query);
        var counts = this.CreateOverviewWorkerCounts(definitionIds);
        return Task.FromResult(new WorkSystemFailedWorkersOverview(
            counts.ActiveWorkerCount,
            counts.FinalWorkerCount,
            counts.FailedWorkerCount,
            counts.WorkerCountByState,
            this.CreateOverviewFailedWorkers(definitionIds)));
    }

    public Task<IReadOnlyList<WorkerIterationOverviewItem>> GetSystemOverviewFailedIterations(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkerIterationOverviewItem>>(this.CreateOverviewFailedIterations(this.ResolveDefinitionScope(query)));
    }

    public Task<IReadOnlyList<WorkerIterationOverviewItem>> GetSystemOverviewCompletedIterations(
        WorkOverviewQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkerIterationOverviewItem>>(this.CreateOverviewCompletedIterations(this.ResolveDefinitionScope(query)));
    }

    private WorkSystemWorkerCounts CreateOverviewWorkerCounts(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var counts = this.index.CountByState(definitionIds);
        var final = counts
            .Where(count => WorkerStateMachine.IsFinal(count.Key))
            .Sum(count => count.Value);
        var active = counts
            .Where(count => IsActiveForSummary(count.Key))
            .Sum(count => count.Value);
        return new WorkSystemWorkerCounts(
            active,
            final,
            counts.GetValueOrDefault(WorkerState.Failed),
            counts);
    }

    private WorkSystemIterationCounts CreateOverviewIterationCounts(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var counts = this.iterationIndex.CountByStatus(definitionIds);
        return new WorkSystemIterationCounts(
            counts.GetValueOrDefault(WorkCompletionStatus.Executing),
            counts.GetValueOrDefault(WorkCompletionStatus.Completed),
            counts.GetValueOrDefault(WorkCompletionStatus.Failed),
            counts.GetValueOrDefault(WorkCompletionStatus.Canceled),
            counts);
    }

    private IReadOnlyList<WorkIterationKeyTypeFacet> CreateOverviewCommonKeyTypes(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => [.. this.iterationIndex.CommonKeyTypes(OverviewCommonKeyTypeCount, definitionIds)
            .Select(keyType => new WorkIterationKeyTypeFacet(
                keyType.Type,
                keyType.IterationCount,
                keyType.IterationCountByKind))];

    private WorkSystemThroughput CreateOverviewThroughput(
        IReadOnlySet<WorkDefinitionId>? definitionIds = null,
        WorkThroughputQuery? throughputQuery = null)
        => this.metrics.GetThroughput(throughputQuery, definitionIds);

    private WorkComponentResult CreateComponent(
        WorkComponentRequest request,
        WorkOverviewQuery? query,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        try
        {
            var data = request.Type.Trim().ToLowerInvariant() switch
            {
                "system" => new
                {
                    SystemName = this.workSystemName,
                    SystemState = this.getSystemState(),
                },
                "catalog" => this.CreateCatalogComponent(query),
                "workers" => this.CreateWorkerSummaryComponent(definitionIds),
                "failedworkers" => this.CreateOverviewFailedWorkers(definitionIds),
                "relationships" => this.CreateRelationshipsComponent(definitionIds),
                "failediterations" => this.CreateOverviewFailedIterations(definitionIds),
                "completediterations" => this.CreateOverviewCompletedIterations(definitionIds),
                "throughput" => this.CreateThroughputComponent(definitionIds, request.Options),
                _ => null,
            };

            return data is null
                ? new WorkComponentResult("error", Error: $"Unknown component '{request.Type}'.")
                : new WorkComponentResult("ok", data);
        }
        catch (Exception exception)
        {
            return new WorkComponentResult("error", Error: exception.Message);
        }
    }

    private object CreateCatalogComponent(WorkOverviewQuery? query)
    {
        var catalogLevel = this.CreateOverviewCatalogLevel(query);
        return new
        {
            CatalogCategories = catalogLevel.Categories,
            CatalogDefinitions = catalogLevel.Definitions,
        };
    }

    private object CreateWorkerSummaryComponent(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var workerCounts = this.CreateOverviewWorkerCounts(definitionIds);
        return new
        {
            DefinitionCount = this.index.ActiveOrQueuedDefinitionCount(definitionIds),
            workerCounts.ActiveWorkerCount,
            workerCounts.FinalWorkerCount,
            workerCounts.FailedWorkerCount,
            workerCounts.WorkerCountByState,
        };
    }

    private object CreateThroughputComponent(
        IReadOnlySet<WorkDefinitionId>? definitionIds,
        JsonElement? options)
    {
        var workerCounts = this.CreateOverviewWorkerCounts(definitionIds);
        return new
        {
            ActiveWorkerCount = workerCounts.ActiveWorkerCount,
            Throughput = this.CreateOverviewThroughput(definitionIds, CreateThroughputQuery(options)),
        };
    }

    private object CreateRelationshipsComponent(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var iterationCounts = this.CreateOverviewIterationCounts(definitionIds);
        return new
        {
            iterationCounts.CurrentIterationCount,
            iterationCounts.CompletedIterationCount,
            iterationCounts.FailedIterationCount,
            iterationCounts.CanceledIterationCount,
            iterationCounts.IterationCountByStatus,
            CommonKeyTypes = this.CreateOverviewCommonKeyTypes(definitionIds),
        };
    }

    private static IReadOnlyList<WorkComponentRequest> NormalizeComponentRequests(
        IReadOnlyList<WorkComponentRequest>? requests)
        => requests is { Count: > 0 }
            ? requests
            : [
                new("system", "system"),
                new("catalog", "catalog"),
                new("workers", "workers"),
                new("failedWorkers", "failedWorkers"),
                new("relationships", "relationships"),
                new("failedIterations", "failedIterations"),
                new("completedIterations", "completedIterations"),
            ];

    private static WorkThroughputQuery? CreateThroughputQuery(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var windowSeconds = TryGetInt32(options.Value, "windowSeconds") ??
            WorkThroughputQuery.DefaultWindowSeconds;
        var bucketSeconds = TryGetInt32(options.Value, "bucketSeconds") ??
            WorkThroughputQuery.DefaultBucketSeconds;
        return new WorkThroughputQuery(windowSeconds, bucketSeconds);
    }

    private static int? TryGetInt32(JsonElement options, string propertyName)
        => options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value)
                ? value
                : null;

    private IReadOnlyList<WorkerOverviewItem> CreateOverviewFailedWorkers(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => [.. this.GetOverviewItems(this.index.ByState(WorkerState.Failed, definitionIds))
            .Take(OverviewWorkerListSize)];

    private IReadOnlyList<WorkerIterationOverviewItem> CreateOverviewFailedIterations(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => this.iterationIndex.RecentByStatus(WorkCompletionStatus.Failed, OverviewIterationListSize, definitionIds);

    private IReadOnlyList<WorkerIterationOverviewItem> CreateOverviewCompletedIterations(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => this.iterationIndex.RecentByStatus(WorkCompletionStatus.Completed, OverviewIterationListSize, definitionIds);

    private OverviewCatalogLevel CreateOverviewCatalogLevel(WorkOverviewQuery? query)
    {
        IReadOnlyList<string> pathSegments = string.IsNullOrWhiteSpace(query?.Category)
            ? []
            : SplitCategoryPath(query.Category);
        var categories = new Dictionary<string, WorkOverviewCatalogCategoryItem>(StringComparer.OrdinalIgnoreCase);
        var directDefinitions = new List<WorkOverviewDefinitionItem>();

        foreach (var definition in this.catalog.Definitions)
        {
            var definitionSegments = SplitCategoryPath(definition.Category);
            if (!StartsWithCategoryPath(definitionSegments, pathSegments))
            {
                continue;
            }

            var remainingSegments = definitionSegments.Skip(pathSegments.Count).ToArray();
            if (remainingSegments.Length == 0)
            {
                directDefinitions.Add(new WorkOverviewDefinitionItem(
                    definition.Id,
                    definition.Name,
                    definition.Category));
                continue;
            }

            var childSegments = pathSegments.Append(remainingSegments[0]).ToArray();
            var childPath = string.Join(':', childSegments);
            if (categories.TryGetValue(childPath, out var existing))
            {
                categories[childPath] = existing with { Count = existing.Count + 1 };
            }
            else
            {
                categories[childPath] = new WorkOverviewCatalogCategoryItem(
                    remainingSegments[0],
                    childPath,
                    1);
            }
        }

        return new OverviewCatalogLevel(
            [.. categories.Values.OrderBy(category => category.Label, StringComparer.OrdinalIgnoreCase)],
            [.. directDefinitions
                .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private IReadOnlyList<WorkerOverviewItem> GetOverviewItems(
        IEnumerable<WorkerId> workerIds,
        IReadOnlySet<WorkerState>? states = null)
        => [.. workerIds
            .Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker : null)
            .OfType<WorkerRecord>()
            .Select(worker => worker.ToOverviewItem())
            .Where(worker => states is null || states.Contains(worker.State))
            .OrderByDescending(worker => worker.UpdatedAt)];

    private IReadOnlySet<WorkDefinitionId>? ResolveDefinitionScope(WorkOverviewQuery? query)
    {
        if (query is null ||
            (query.DefinitionId is null &&
            string.IsNullOrWhiteSpace(query.DefinitionName) &&
            string.IsNullOrWhiteSpace(query.Category)))
        {
            return null;
        }

        return this.GetDefinitionScopeCandidates(query)
            .Where(definition => Matches(definition, query))
            .Select(definition => definition.Id)
            .ToHashSet();
    }

    private IEnumerable<WorkDefinition> GetDefinitionScopeCandidates(WorkOverviewQuery query)
    {
        if (query.DefinitionId is { } definitionId)
        {
            return this.catalog.TryGet(definitionId, out var definition) ? [definition] : [];
        }

        if (!string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            return this.catalog.TryGet(query.DefinitionName, out var definition) ? [definition] : [];
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            return this.catalog.ListByCategory(query.Category, query.IncludeSubcategories);
        }

        return this.catalog.Definitions;
    }

    private IEnumerable<WorkerRecord> GetCandidateWorkers(WorkerQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            if (!this.catalog.TryGet(query.DefinitionName, out var definition))
            {
                return [];
            }

            query = query with
            {
                DefinitionId = definition.Id,
            };
        }

        IReadOnlySet<WorkDefinitionId>? definitionIds = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            definitionIds = this.catalog
                .ListByCategory(query.Category, query.IncludeSubcategories)
                .Select(definition => definition.Id)
                .ToHashSet();
        }

        var candidateIds = this.index.FindBestCandidates(query, definitionIds);
        return candidateIds is null
            ? this.workers.Values
            : candidateIds.Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker : null)
                .OfType<WorkerRecord>();
    }

    private IReadOnlyList<WorkerRecord> GetBulkActionCandidates(WorkerBulkActionFilter filter)
    {
        if (string.IsNullOrWhiteSpace(filter.Category))
        {
            return [.. this.workers.Values];
        }

        var definitionIds = this.catalog
            .ListByCategory(filter.Category, filter.IncludeSubcategories)
            .Select(definition => definition.Id)
            .ToHashSet();
        if (definitionIds.Count == 0)
        {
            return [];
        }

        return [.. definitionIds
            .SelectMany(definitionId => this.index.ByDefinition(definitionId))
            .Distinct()
            .Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker : null)
            .OfType<WorkerRecord>()];
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
            summaries.Count(worker => IsActiveForSummary(worker.State)),
            summaries.Count(worker => worker.State == WorkerState.Queued),
            summaries.Count(worker => worker.State is WorkerState.Running or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling),
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

    private static bool Matches(WorkerOverviewItem worker, WorkerQuery query)
        => (query.DefinitionId is null || worker.DefinitionId == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(worker.DefinitionName, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(worker.Category, query.Category, query.IncludeSubcategories)) &&
            (query.SubjectId is null || worker.SubjectId == query.SubjectId) &&
            (query.ConcurrencyKey is null || worker.ConcurrencyKey == query.ConcurrencyKey) &&
            (query.Identifier is null || worker.Identifiers.Contains(query.Identifier.Value)) &&
            (query.States is null || query.States.Contains(worker.State)) &&
            (query.CreatedFrom is null || worker.CreatedAt >= query.CreatedFrom) &&
            (query.CreatedTo is null || worker.CreatedAt <= query.CreatedTo) &&
            (query.UpdatedFrom is null || worker.UpdatedAt >= query.UpdatedFrom) &&
            (query.UpdatedTo is null || worker.UpdatedAt <= query.UpdatedTo);

    private static bool Matches(WorkerRecord worker, WorkerConfigurationQuery? query)
        => query is null ||
            (query.RecurrenceEnabled is null || worker.Configuration.Recurrence.IsEnabled == query.RecurrenceEnabled) &&
            (query.ConcurrencyEnabled is null || worker.Configuration.Concurrency.IsEnabled == query.ConcurrencyEnabled) &&
            (query.ProfilingEnabled is null || worker.Options.ProfilingEnabled == query.ProfilingEnabled);

    private static bool Matches(WorkerIterationIndex.IndexedWorkerIteration iteration, WorkerIterationQuery query)
        => (query.WorkerId is null || iteration.WorkerId == query.WorkerId) &&
            (query.DefinitionId is null || iteration.DefinitionId == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(iteration.DefinitionName, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(iteration.Category, query.Category, includeSubcategories: true)) &&
            (query.SubjectId is null || iteration.SubjectId == query.SubjectId) &&
            (query.ConcurrencyKey is null || iteration.ConcurrencyKey == query.ConcurrencyKey) &&
            (query.Identifier is null || iteration.Identifiers.Contains(query.Identifier.Value)) &&
            (query.Statuses is null || query.Statuses.Contains(iteration.Status)) &&
            (query.StartedFrom is null || iteration.StartedAt >= query.StartedFrom) &&
            (query.StartedTo is null || iteration.StartedAt <= query.StartedTo) &&
            (query.CompletedFrom is null || iteration.CompletedAt >= query.CompletedFrom) &&
            (query.CompletedTo is null || iteration.CompletedAt <= query.CompletedTo);

    private static bool Matches(WorkDefinition definition, WorkDefinitionQuery query)
        => (query.Id is null || definition.Id == query.Id) &&
            (string.IsNullOrWhiteSpace(query.Name) || string.Equals(definition.Name, query.Name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(definition.Category, query.Category, query.IncludeSubcategories)) &&
            (string.IsNullOrWhiteSpace(query.Search) ||
                definition.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                (definition.Description?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool Matches(WorkDefinition definition, WorkOverviewQuery query)
        => (query.DefinitionId is null || definition.Id == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(definition.Name, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(definition.Category, query.Category, query.IncludeSubcategories));

    private static bool Matches(WorkerIndex.IndexedWorkKey key, WorkerKeyQuery query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Value) || string.Equals(key.Value, query.Value, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: true);

    private static bool Matches(WorkerIndex.IndexedWorkKey key, WorkerKeyTypeQuery query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: false);

    private static bool Matches(WorkerIterationIndex.IndexedWorkIterationKey key, WorkIterationKeyQuery query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Value) || string.Equals(key.Value, query.Value, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: true);

    private static bool Matches(WorkerIterationIndex.IndexedWorkIterationKey key, WorkIterationKeyTypeQuery query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: false);

    private IReadOnlyDictionary<WorkKeyKind, int> CountWorkersByKind(
        IEnumerable<WorkerIndex.IndexedWorkKey> keys,
        IReadOnlySet<WorkerState>? states)
        => keys
            .GroupBy(key => key.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Count = this.GetOverviewItems(group.SelectMany(key => key.WorkerIds).Distinct(), states).Count,
            })
            .Where(count => count.Count > 0)
            .ToDictionary(count => count.Kind, count => count.Count);

    private IReadOnlyDictionary<WorkKeyKind, int> CountIterationsByKind(
        IEnumerable<WorkerIterationIndex.IndexedWorkIterationKey> keys,
        IReadOnlySet<WorkCompletionStatus>? statuses)
        => keys
            .GroupBy(key => key.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Count = this.iterationIndex.GetOverviewItems(
                    group.SelectMany(key => key.IterationReferences).Distinct(),
                    statuses).Count,
            })
            .Where(count => count.Count > 0)
            .ToDictionary(count => count.Kind, count => count.Count);

    private static bool MatchesWorkKeySearch(
        string type,
        string value,
        string? search,
        bool includeValue)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var terms = SearchTerms(search);
        if (terms.Count == 0)
        {
            return true;
        }

        return terms.All(term =>
            type.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (includeValue && value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<string> SearchTerms(string search)
    {
        var terms = new List<string>();
        foreach (var term in search.Split(
            [' ', '\t', '\r', '\n', '.', ',', ':', ';', '-', '_', '/', '\\', '#', '=', '&', '?'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsIgnoredWorkKeySearchTerm(term))
            {
                continue;
            }

            terms.Add(term);
        }

        return terms;
    }

    private static bool IsIgnoredWorkKeySearchTerm(string term)
        => term.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("for", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("keys", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("the", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("work", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("worker", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("workers", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeWorkKeyTake(int take)
        => take <= 0 ? WorkerKeyQuery.DefaultTake : Math.Min(take, WorkerKeyQuery.MaximumTake);

    private static int NormalizeWorkIterationKeyTake(int take)
        => take <= 0 ? WorkIterationKeyQuery.DefaultTake : Math.Min(take, WorkIterationKeyQuery.MaximumTake);

    private static bool CategoryMatches(string actual, string expected, bool includeSubcategories)
        => includeSubcategories
            ? actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                actual.StartsWith($"{expected}:", StringComparison.OrdinalIgnoreCase)
            : actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SplitCategoryPath(string? category)
        => (string.IsNullOrWhiteSpace(category)
                ? WorkDefinitionMetadataDefaults.Category
                : category)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool StartsWithCategoryPath(
        IReadOnlyList<string> categorySegments,
        IReadOnlyList<string> pathSegments)
        => pathSegments.Count == 0 ||
            pathSegments.Count <= categorySegments.Count &&
            pathSegments
                .Select((segment, index) => string.Equals(
                    categorySegments[index],
                    segment,
                    StringComparison.OrdinalIgnoreCase))
                .All(matches => matches);

    private sealed record OverviewCatalogLevel(
        IReadOnlyList<WorkOverviewCatalogCategoryItem> Categories,
        IReadOnlyList<WorkOverviewDefinitionItem> Definitions);

    private static IEnumerable<WorkerOverviewItem> Sort(
        IEnumerable<WorkerOverviewItem> workers,
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

    private static IEnumerable<WorkerIterationOverviewItem> Sort(
        IEnumerable<WorkerIterationOverviewItem> iterations,
        WorkerIterationQuerySort sort,
        WorkQuerySortDirection direction)
    {
        var ascending = direction == WorkQuerySortDirection.Ascending;
        return sort switch
        {
            WorkerIterationQuerySort.StartedAt => ascending ? iterations.OrderBy(iteration => iteration.StartedAt) : iterations.OrderByDescending(iteration => iteration.StartedAt),
            WorkerIterationQuerySort.ExecutionDuration => ascending ? iterations.OrderBy(iteration => iteration.ExecutionDuration) : iterations.OrderByDescending(iteration => iteration.ExecutionDuration),
            WorkerIterationQuerySort.DefinitionName => ascending ? iterations.OrderBy(iteration => iteration.DefinitionName) : iterations.OrderByDescending(iteration => iteration.DefinitionName),
            WorkerIterationQuerySort.Status => ascending ? iterations.OrderBy(iteration => iteration.Status) : iterations.OrderByDescending(iteration => iteration.Status),
            _ => ascending ? iterations.OrderBy(iteration => iteration.CompletedAt) : iterations.OrderByDescending(iteration => iteration.CompletedAt),
        };
    }

    private IReadOnlyList<WorkerRecord> CancelActive(WorkOrigin origin)
    {
        var canceledWorkers = new List<WorkerRecord>();
        foreach (var worker in this.workers.Values.Where(worker => ShouldCancelForSystemStop(worker.State)))
        {
            var outcome = worker.RequestCancelForSystemStop();
            if (outcome.IsAccepted)
            {
                canceledWorkers.Add(worker);
                this.HandleAcceptedWorkerChange(worker, WorkAction.Cancel);
                worker.RecordActionHistory(outcome, origin);
                this.SynchronizeWorkerIfTracked(worker);
                worker.SignalCurrentCompletion();
                this.workerEvents.ActionApplied(worker, outcome, origin);
            }
        }

        return canceledWorkers;
    }

    private static bool ShouldCancelForSystemStop(WorkerState state)
        => state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying;

    private void ClearWorkerMemory()
    {
        this.workers.Clear();
        this.index.Clear();
        this.iterationIndex.Clear();
        this.concurrency.Clear();
        this.dispatcher.ClearScheduledWork();
        this.retention.Clear();
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

    private IReadOnlyList<WorkerSnapshot> ForceCancelRemaining(
        IReadOnlyList<WorkerRecord> canceledWorkers,
        WorkOrigin origin)
    {
        var forceCanceledWorkers = new List<WorkerSnapshot>();
        foreach (var worker in canceledWorkers.Where(worker => !worker.IsFinal))
        {
            var outcome = worker.ForceCancelForSystemStop();
            if (!outcome.IsAccepted)
            {
                continue;
            }

            this.HandleAcceptedWorkerChange(worker, WorkAction.Cancel);
            worker.RecordActionHistory(outcome, origin);
            this.SynchronizeWorkerIfTracked(worker);
            worker.SignalCurrentCompletion();
            this.workerEvents.ActionApplied(worker, outcome, origin);
            this.workerEvents.CompletionRecorded(worker, WorkCompletionStatus.Canceled);
            if (outcome.Worker is { } snapshot)
            {
                forceCanceledWorkers.Add(snapshot);
            }
        }

        return forceCanceledWorkers;
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
        this.iterationIndex.Forget(worker);
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
        lock (this.lifecycleSync)
        {
            if (!this.acceptingWork)
            {
                return;
            }
        }

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

        public void Clear()
        {
            lock (this.sync)
            {
                this.scheduledPurges.Clear();
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

        public void Clear()
            => this.managers.Clear();

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
        private Task<DispatcherCompletion>? dispatchTask;

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

        public void ClearScheduledWork()
        {
            while (this.scheduledWorkers.Reader.TryRead(out _))
            {
                continue;
            }
        }

        public async Task Stop(CancellationToken cancellationToken)
        {
            Task<DispatcherCompletion>? task;
            lock (this.sync)
            {
                this.cancellation?.Cancel();
                task = this.dispatchTask;
            }

            if (task is null)
            {
                return;
            }

            await WaitForDispatcherCompletion(task, cancellationToken);
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

        private async Task<DispatcherCompletion> Run(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var worker in this.scheduledWorkers.Reader.ReadAllAsync(cancellationToken))
                {
                    await dispatch(worker, cancellationToken);
                }

                return DispatcherCompletion.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DispatcherCompletion.ShutdownCanceled;
            }
        }

        private static async Task<DispatcherCompletion> WaitForDispatcherCompletion(
            Task<DispatcherCompletion> task,
            CancellationToken cancellationToken)
        {
            if (!task.IsCompleted)
            {
                var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.UnsafeRegister(
                    static state =>
                    {
                        if (state is TaskCompletionSource cancellation)
                        {
                            cancellation.TrySetResult();
                        }
                    },
                    cancellation);

                if (await Task.WhenAny(task, cancellation.Task) != task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return await task;
        }

        private enum DispatcherCompletion
        {
            Completed,
            ShutdownCanceled,
        }
    }
}

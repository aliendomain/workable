using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkerOperations : IWorkerOperations, IDisposable
{
    private const int IterationIndexRecentCapacity = 5;

    private readonly WorkSystemCatalog catalog;
    private readonly Func<WorkSystemState> getSystemState;
    private readonly string? workSystemName;
    private readonly WorkerEventPublisher workerEvents;
    private readonly ConfiguredWorkerExecutionStrategy executionStrategy;
    private readonly ConcurrentDictionary<WorkerId, WorkerRecord> workers = [];
    private readonly WorkerIndex index = new();
    private readonly WorkerIterationIndex iterationIndex = new(IterationIndexRecentCapacity);
    private readonly InMemoryWorkMetricsSink metrics;
    private readonly WorkQueryService queries;
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
        this.queries = new WorkQueryService(
            this.catalog,
            this.getSystemState,
            this.workSystemName,
            this.workers,
            this.index,
            this.iterationIndex,
            this.metrics);
    }

    internal WorkQueryService Queries => this.queries;

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

    private List<WorkerRecord> CancelActive(WorkOrigin origin)
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

    private List<WorkerSnapshot> ForceCancelRemaining(
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
                bypassConcurrencyWhenFlexible,
                out var executionToken,
                systemExecutionCancellationToken)
            : worker.Start(expectedRevision, advancesRevision, out executionToken, systemExecutionCancellationToken);
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
}

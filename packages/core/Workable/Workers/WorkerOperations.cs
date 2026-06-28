using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkerOperations :
    IWorkerOperations,
    IDisposable
{
    private readonly WorkSystemCatalog catalog;
    private readonly Func<WorkSystemState> getSystemState;
    private readonly WorkerEventPublisher workerEvents;
    private readonly WorkerIterationTransitionCoordinator iterationTransitions;
    private readonly ConfiguredWorkerExecutionStrategy executionStrategy;
    private readonly ConcurrentDictionary<WorkerId, WorkerRecord> workers = [];
    private readonly WorkerIndex index = new();
    private readonly WorkerPersistenceCoordinator persistence;
    private readonly ConcurrentDictionary<WorkerIterationReference, WorkCompletionStatus> iterationStatuses = [];
    private readonly InMemoryWorkMetricsSink metrics;
    private readonly IWorkSystemReadModelStore readModel;
    private readonly Lock lifecycleSync = new();
    private readonly List<CancellationTokenSource> retiredSystemExecutionLifetimes = [];
    private readonly WorkerDispatcher dispatcher;
    private readonly WorkConcurrencyCoordinator concurrency;
    private readonly FailedWorkerAutoCancelScheduler failedWorkerAutoCancel;
    private readonly WorkerRetentionScheduler retention;
    private readonly TimeSpan shutdownGracePeriod;
    private readonly WorkSystemCapacityConfiguration capacity;
    private readonly WorkSystemQueueDiagnosticsTracker queueDiagnostics;
    private readonly bool persistenceStoreAvailable;
    private CancellationTokenSource systemExecutionLifetime = new();
    private volatile bool acceptingWork;
    private long workerCount;
    private long finalWorkerCount;
    private readonly ConcurrentDictionary<WorkerId, byte> finalCapacityWorkers = [];

    internal WorkerOperations(
        WorkSystemCatalog catalog,
        Func<WorkSystemState> getSystemState,
        WorkSystemId workSystemId,
        string? workSystemName,
        IServiceProvider rootServices,
        WorkEventStream events,
        IWorkSystemReadModelStore readModel,
        IReadOnlyList<WorkExceptionClassifier> systemExceptionClassifiers,
        IReadOnlyList<WorkExceptionClassifier> globalExceptionClassifiers,
        TimeSpan shutdownGracePeriod,
        WorkSystemRetentionConfiguration retentionConfiguration,
        WorkSystemCapacityConfiguration capacity,
        InMemoryWorkMetricsSink metrics,
        WorkSystemQueueDiagnosticsTracker queueDiagnostics,
        WorkSystemIdempotencyDiagnosticsTracker idempotencyDiagnostics,
        IWorkPersistenceStore? persistenceStore)
    {
        this.catalog = catalog;
        this.getSystemState = getSystemState;
        this.shutdownGracePeriod = shutdownGracePeriod;
        this.capacity = capacity;
        this.metrics = metrics;
        this.queueDiagnostics = queueDiagnostics;
        this.persistenceStoreAvailable = persistenceStore is not null;
        this.readModel = readModel;
        this.concurrency = new WorkConcurrencyCoordinator();
        var persistenceLogger = rootServices.GetService<ILoggerFactory>()?.CreateLogger("Workable.Persistence");
        var durabilityOptions = rootServices.GetService<WorkQueueDurabilityRuntimeOptions>() ??
            WorkQueueDurabilityRuntimeOptions.Default;
        this.persistence = new WorkerPersistenceCoordinator(
            catalog,
            this.workers,
            this.index,
            this.concurrency,
            workSystemId,
            workSystemName,
            persistenceStore,
            idempotencyDiagnostics,
            this.IsAcceptingWork,
            this.GetSystemExecutionLifetimeToken,
            this.AcceptWorkerIntoMemory,
            this.GetTrackedWorker,
            this.OnPersistedWorkerMaterialized,
            this.InterruptWorker,
            persistenceLogger,
            durabilityOptions);
        this.workerEvents = new WorkerEventPublisher(workSystemId, workSystemName, events, this.SynchronizeWorkerIfTracked, readModel);
        var logger = rootServices.GetService<ILoggerFactory>()?.CreateLogger("Workable.WorkerExecution");
        var invoker = new WorkerExecutionInvoker(
            workSystemId,
            workSystemName,
            rootServices,
            this.persistence,
            this.workerEvents,
            this.AddIdentifier,
            new WorkInitializationExecutor(rootServices));
        var exceptionHandler = new WorkerExecutionExceptionHandler(
            new WorkExceptionClassifierChain(systemExceptionClassifiers, globalExceptionClassifiers, logger),
            logger);
        var attemptRunner = new WorkerExecutionAttemptRunner(invoker, exceptionHandler);
        var completionRecorder = new WorkerExecutionCompletionRecorder(this.workerEvents);
        this.iterationTransitions = new WorkerIterationTransitionCoordinator(this.workerEvents);
        var runOnce = new RunOnceWorkerExecutionStrategy(
            attemptRunner,
            completionRecorder);
        var transientRetry = new TransientRetryWorkerExecutionStrategy(
            attemptRunner,
            completionRecorder,
            this.workerEvents,
            this.iterationTransitions);
        var recurring = new RecurringWorkerExecutionStrategy(
            attemptRunner,
            completionRecorder,
            this.workerEvents,
            this.iterationTransitions);
        this.executionStrategy = new ConfiguredWorkerExecutionStrategy(runOnce, transientRetry, recurring);
        this.dispatcher = new WorkerDispatcher(this.DispatchQueuedWorker);
        var failedWorkerLogger = rootServices.GetService<ILoggerFactory>()?.CreateLogger("Workable.FailedWorkerHandling");
        this.failedWorkerAutoCancel = new FailedWorkerAutoCancelScheduler(this.AutoCancelFailedWorkers, failedWorkerLogger);
        this.retention = new WorkerRetentionScheduler(this.index, retentionConfiguration, this.PurgeFinalWorkersForRetention);
    }

    internal WorkSystemRetentionDiagnostics RetentionDiagnostics => this.retention.Diagnostics;

    internal WorkSystemConcurrencyDiagnostics ConcurrencyDiagnostics => this.concurrency.Diagnostics;

    internal WorkSystemDurabilityDiagnostics DurabilityDiagnostics => this.persistence.DurabilityDiagnostics;

    internal WorkSystemIdempotencyDiagnostics IdempotencyDiagnostics => this.persistence.IdempotencyDiagnostics;

    internal async Task<IWorkerHandle> CreateWorker(
        RegisteredWork registeredWork,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!this.TryAcceptWork(out var rejection))
        {
            return this.RejectQueue(rejection);
        }

        var storedRequestContext = requestContext.WithoutAuthorization();

        var workerId = WorkerId.New();
        var runtimePlan = options is null
            ? registeredWork.DefaultRuntimePlan
            : RegisteredWorkRuntimePlan.Create(registeredWork.Definition, options);
        if (runtimePlan.ConfigurationErrors.Count > 0)
        {
            return this.RejectQueue(WorkQueueOutcome.Invalid(runtimePlan.ConfigurationErrors));
        }

        var persistenceStoreErrors = WorkConfigurationValidator.ValidatePersistenceStore(
            runtimePlan.Configuration,
            this.persistenceStoreAvailable);
        if (persistenceStoreErrors.Count > 0)
        {
            return this.RejectQueue(WorkQueueOutcome.Invalid(persistenceStoreErrors));
        }

        var concurrencyInputErrors = WorkConfigurationValidator.ValidateConcurrencyInput(
            coordination: runtimePlan.Configuration.Coordination,
            input: input);
        if (concurrencyInputErrors.Count > 0)
        {
            return this.RejectQueue(WorkQueueOutcome.Invalid(concurrencyInputErrors));
        }

        var acceptance = await this.persistence.AcceptQueuedWorker(
            workerId,
            registeredWork,
            input,
            runtimePlan,
            storedRequestContext,
            DateTimeOffset.UtcNow,
            cancellationToken);

        if (!acceptance.Outcome.IsAccepted)
        {
            return this.RejectQueue(acceptance.Outcome);
        }

        if (acceptance.Handle is { } durableHandle)
        {
            return durableHandle;
        }

        var record = acceptance.Worker ?? throw new InvalidOperationException("Accepted queue operation did not include a worker.");

        this.workerEvents.Queued(record);
        var handle = new WorkerHandle(acceptance.Outcome, record);

        if (acceptance.ShouldScheduleStart)
        {
            this.ScheduleStart(record);
        }

        if (acceptance.ShouldDrainQueuedWorkers)
        {
            this.ScheduleConcurrencyDrain(registeredWork.Definition.Id);
        }

        await WaitForStartPolicy(record, runtimePlan.StartPolicy, cancellationToken);
        return handle;
    }

    internal IWorkerHandle RejectQueue(WorkQueueOutcome rejection)
    {
        this.queueDiagnostics.RecordRejected(rejection);
        return WorkerHandle.Rejected(rejection);
    }

    private bool TryAcceptWork([NotNullWhen(false)] out WorkQueueOutcome? rejection)
    {
        lock (this.lifecycleSync)
        {
            var systemState = this.getSystemState();
            if (systemState is WorkSystemState.Stopping)
            {
                rejection = WorkQueueOutcome.Invalid(
                    [WorkMessage.Warning(
                        "workable.system.stopping",
                        "Workable is stopping and is not accepting new work.",
                        "system")]);
                return false;
            }

            if (systemState is not WorkSystemState.Started)
            {
                rejection = WorkQueueOutcome.Invalid(
                    [WorkMessage.Warning(
                        "workable.system.not_started",
                        $"Workable is '{systemState}' and is not accepting new work.",
                        "system")]);
                return false;
            }

            if (!this.acceptingWork)
            {
                rejection = WorkQueueOutcome.Invalid(
                    [WorkMessage.Warning(
                        "workable.system.stopping",
                        "Workable is stopping and is not accepting new work.",
                        "system")]);
                return false;
            }
        }

        if (this.GetNonFinalWorkerCount() >= this.capacity.MaximumWorkers)
        {
            rejection = WorkQueueOutcome.Invalid(
                [WorkMessage.Warning(
                    "workable.system.capacity_reached",
                    $"Workable has reached the configured maximum non-final worker count of {this.capacity.MaximumWorkers}.",
                    "system.capacity.maximumWorkers")]);
            return false;
        }

        rejection = null;
        return true;
    }

    private void RegisterIterationIfTracked(WorkerReadModelIterationUpdate iteration)
    {
        if (this.workers.ContainsKey(iteration.Worker.Id))
        {
            var reference = iteration.Iteration.Reference;
            if (this.TryRecordIterationStatus(reference, iteration.Iteration.Status))
            {
                this.metrics.IterationRecorded(iteration.Iteration.DefinitionId, iteration.Snapshot);
            }

            this.readModel.RecordIteration(iteration);
        }
    }

    private void ForgetIterationIfTracked(WorkerIterationReference iteration)
    {
        if (this.workers.ContainsKey(iteration.WorkerId))
        {
            this.iterationStatuses.TryRemove(iteration, out _);
            this.readModel.ForgetIteration(iteration);
        }
    }

    private bool TryRecordIterationStatus(
        WorkerIterationReference reference,
        WorkCompletionStatus status)
    {
        while (true)
        {
            if (!this.iterationStatuses.TryGetValue(reference, out var existing))
            {
                if (this.iterationStatuses.TryAdd(reference, status))
                {
                    return true;
                }

                continue;
            }

            if (existing == status)
            {
                return false;
            }

            if (this.iterationStatuses.TryUpdate(reference, status, existing))
            {
                return true;
            }
        }
    }

    private void AddIdentifier(WorkerRecord worker, WorkIdentifier _)
    {
        this.readModel.RecordWorker(worker.ToReadModelWorker());
    }

    private void AttachIndexCallbacks(WorkerRecord worker)
    {
        worker.IterationRecorded = this.RegisterIterationIfTracked;
        worker.IterationForgotten = this.ForgetIterationIfTracked;
    }

    private void AcceptWorkerIntoMemory(WorkerRecord worker)
    {
        this.AttachIndexCallbacks(worker);
        this.TrackWorker(worker);
        this.index.Register(worker);
    }

    private void SynchronizeWorkerIfTracked(WorkerRecord worker)
    {
        if (this.workers.ContainsKey(worker.Id))
        {
            this.index.Synchronize(worker);
        }
    }

    internal async Task StartDispatching(CancellationToken cancellationToken)
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

        await this.persistence.InitializeAndDrain(
            [.. this.catalog.RegisteredWork.Select(work => work.Definition)],
            cancellationToken);

        this.dispatcher.Start(this.GetSystemExecutionLifetimeToken());
        this.failedWorkerAutoCancel.Start();
        this.retention.Start();
        this.persistence.StartBackgroundTasks();
    }

    internal Task<WorkSystemStopResult> StopDispatching(CancellationToken cancellationToken)
        => this.StopDispatching(
            new WorkRequestContext(WorkOrigin.Create(WorkInvocationChannel.InProcess)),
            cancellationToken);

    internal async Task<WorkSystemStopResult> StopDispatching(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        lock (this.lifecycleSync)
        {
            this.acceptingWork = false;
        }

        var interruptedWorkers = this.InterruptActiveForSystemStop();
        await this.dispatcher.Stop(cancellationToken);
        await this.failedWorkerAutoCancel.Stop(cancellationToken);
        await this.retention.Stop(cancellationToken);
        await this.WaitForInterruptedWorkers(interruptedWorkers, cancellationToken);
        var forceInterruptedWorkers = this.ForceInterruptRemaining(interruptedWorkers);
        lock (this.lifecycleSync)
        {
            this.systemExecutionLifetime.Cancel();
        }

        await this.persistence.StopBackgroundTasks(cancellationToken);
        this.ClearWorkerMemory();
        return new WorkSystemStopResult(forceInterruptedWorkers)
        {
            CancellationRequestedWorkers = [.. interruptedWorkers.Select(worker => worker.ToSnapshot())],
            CancellationRequestedWorkerSummaries = [.. interruptedWorkers
                .Select(worker => WorkSystemShutdownWorker.From(worker.ToSnapshot()))],
            ForceInterruptedWorkerSummaries = [.. forceInterruptedWorkers
                .Select(WorkSystemShutdownWorker.From)],
            ShutdownGracePeriod = this.shutdownGracePeriod,
        };
    }

    public Task<WorkerSnapshot?> Get(WorkerId workerId, CancellationToken cancellationToken = default)
        => this.GetAuthoritative(workerId, cancellationToken);

    internal Task<WorkerSnapshot?> GetAuthoritative(WorkerId workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.workers.TryGetValue(workerId, out var worker)
            ? worker.ToSnapshot()
            : null);
    }

    internal IWorkerHandle CreateHandle(WorkerId workerId)
        => this.persistence.CreateHandle(
            WorkQueueOutcome.Accepted(workerId),
            this.GetTrackedWorker);

    internal Task<WorkerIterationSnapshot?> GetIterationAuthoritative(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.workers.TryGetValue(iteration.WorkerId, out var worker)
            ? worker.GetIterationSnapshot(iteration.Sequence)
            : null);
    }

    public Task<IReadOnlyList<WorkerSnapshot>> List(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateSnapshotList(this.workers.Values));
    }

    public Task<IReadOnlyList<WorkerSnapshot>> List(WorkSubjectId subjectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.GetSubjectWorkersLocked(subjectId));
    }

    public Task<IReadOnlyList<WorkerSnapshot>> List(
        WorkDefinitionId definitionId,
        WorkSubjectId subjectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.GetSubjectWorkersLocked(definitionId, subjectId));
    }

    public Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.Execute(
            worker,
            action,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);
    }

    internal Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(this.ApplyAction(worker, action, requestContext));
    }

    public Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
        => this.ExecuteAll(
            action,
            filter,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);

    internal Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(requestContext);

        filter ??= WorkerBulkActionFilter.All;
        var candidates = this.GetBulkActionCandidates(filter);
        var outcomes = new List<WorkActionOutcome>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = candidate.ToSummary().Version;
            outcomes.Add(this.ApplyAction(version, action, requestContext));
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
        WorkRequestContext requestContext)
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

        var storedRequestContext = requestContext.WithoutAuthorization();
        record.RecordActionHistory(outcome, storedRequestContext);

        if (outcome.IsAccepted)
        {
            this.HandleAcceptedWorkerChange(record, action);
            if (action != WorkAction.Purge)
            {
                this.SynchronizeWorkerIfTracked(record);
                if (ShouldSignalCurrentCompletion(action))
                {
                    record.SignalCurrentCompletion();
                }
            }
        }

        this.workerEvents.ActionApplied(record, outcome, storedRequestContext);
        return outcome;
    }

    private static bool ShouldSignalCurrentCompletion(WorkAction action)
        => action != WorkAction.Pause;

    public Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.Reconfigure(
            worker,
            changes,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);
    }

    internal Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!this.workers.TryGetValue(worker.WorkerId, out var record))
        {
            return Task.FromResult(WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId));
        }

        var outcome = record.Reconfigure(changes, worker.Revision, this.persistenceStoreAvailable);
        var storedRequestContext = requestContext.WithoutAuthorization();
        record.RecordReconfigurationHistory(changes, outcome, storedRequestContext);
        if (outcome.IsAccepted)
        {
            this.concurrency.Synchronize(record);
            this.SynchronizeFailedWorkerAutoCancel(record);

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
            this.workerEvents.Reconfigured(record, changes, outcome, storedRequestContext);
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

    private List<WorkerRecord> InterruptActiveForSystemStop()
    {
        var interruptedWorkers = new List<WorkerRecord>();
        foreach (var worker in this.workers.Values.Where(worker => ShouldInterruptForSystemStop(worker.State)))
        {
            if (worker.RequestInterruptForSystemStop())
            {
                interruptedWorkers.Add(worker);
                this.HandleAcceptedInterruption(worker);
            }
        }

        return interruptedWorkers;
    }

    private static bool ShouldInterruptForSystemStop(WorkerState state)
        => state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying;

    private void ClearWorkerMemory()
    {
        this.workers.Clear();
        Volatile.Write(ref this.workerCount, 0);
        Volatile.Write(ref this.finalWorkerCount, 0);
        this.finalCapacityWorkers.Clear();
        this.index.Clear();
        this.iterationStatuses.Clear();
        this.readModel.Clear();
        this.concurrency.Clear();
        this.dispatcher.ClearScheduledWork();
        this.failedWorkerAutoCancel.Clear();
        this.retention.Clear();
    }

    private void TrackWorker(WorkerRecord worker)
    {
        if (this.workers.TryAdd(worker.Id, worker))
        {
            Interlocked.Increment(ref this.workerCount);
            this.persistence.SignalAccepted(worker);
        }
    }

    private WorkerRecord? GetTrackedWorker(WorkerId workerId)
        => this.workers.TryGetValue(workerId, out var worker) ? worker : null;

    private void InterruptWorker(WorkerRecord worker, WorkInterruptionReason reason)
    {
        if (worker.RequestInterrupt(reason))
        {
            this.HandleAcceptedInterruption(worker);
        }
    }

    private void HandleAcceptedInterruption(WorkerRecord worker, bool signalCompletion = true)
    {
        this.concurrency.Synchronize(worker);
        this.failedWorkerAutoCancel.Forget(worker.Id);
        this.SynchronizeWorkerIfTracked(worker);
        if (signalCompletion && worker.SignalCurrentCompletion())
        {
            this.workerEvents.CompletionRecorded(worker, WorkCompletionStatus.Interrupted);
        }
    }

    private bool TryRemoveWorker(WorkerId workerId, [NotNullWhen(true)] out WorkerRecord? worker)
    {
        if (!this.workers.TryRemove(workerId, out worker))
        {
            return false;
        }

        Interlocked.Decrement(ref this.workerCount);
        if (this.finalCapacityWorkers.TryRemove(workerId, out _))
        {
            Interlocked.Decrement(ref this.finalWorkerCount);
        }

        return true;
    }

    private long GetNonFinalWorkerCount()
        => Math.Max(
            0,
            Volatile.Read(ref this.workerCount) - Volatile.Read(ref this.finalWorkerCount));

    private void TrackFinalWorkerForCapacity(WorkerRecord worker)
    {
        if (worker.IsFinal && this.finalCapacityWorkers.TryAdd(worker.Id, 0))
        {
            Interlocked.Increment(ref this.finalWorkerCount);
        }
    }

    private async Task WaitForInterruptedWorkers(
        IReadOnlyList<WorkerRecord> interruptedWorkers,
        CancellationToken cancellationToken)
    {
        var pending = interruptedWorkers
            .Where(worker => !worker.IsCompletionSignaled)
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

    private List<WorkerSnapshot> ForceInterruptRemaining(
        IReadOnlyList<WorkerRecord> interruptedWorkers)
    {
        var forceInterruptedWorkers = new List<WorkerSnapshot>();
        foreach (var worker in interruptedWorkers.Where(worker => !worker.IsCompletionSignaled))
        {
            var snapshot = worker.ForceInterruptForSystemStop();
            if (snapshot is null)
            {
                continue;
            }

            this.HandleAcceptedInterruption(worker, signalCompletion: false);
            this.workerEvents.CompletionRecorded(worker, WorkCompletionStatus.Interrupted);
            forceInterruptedWorkers.Add(snapshot);
        }

        return forceInterruptedWorkers;
    }

    public void Dispose()
    {
        lock (this.lifecycleSync)
        {
            this.systemExecutionLifetime.Cancel();
            this.dispatcher.Dispose();
            this.failedWorkerAutoCancel.Dispose();
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
        var outcome = worker.Configuration.Coordination.IsConcurrencyEnabled
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
        this.iterationTransitions.RecordWorkerStarted(worker);
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
        if (action != WorkAction.Purge)
        {
            this.concurrency.Synchronize(worker);
            this.TrackFinalWorkerForCapacity(worker);
        }

        if (action != WorkAction.Purge && worker.IsFinal)
        {
            this.retention.Schedule(worker);
            this.persistence.SynchronizeWorkerState(worker);
        }

        this.SynchronizeFailedWorkerAutoCancel(worker);
        this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
    }

    private void HandleWorkerExecutionCompleted(WorkerRecord worker)
    {
        this.concurrency.Synchronize(worker);
        this.TrackFinalWorkerForCapacity(worker);

        if (worker.IsFinal)
        {
            this.retention.Schedule(worker);
        }

        this.persistence.SynchronizeWorkerState(worker);
        this.SynchronizeFailedWorkerAutoCancel(worker);

        this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
    }

    private WorkActionOutcome Purge(WorkerRecord worker, long expectedRevision)
    {
        var outcome = worker.Purge(expectedRevision);
        if (!outcome.IsAccepted)
        {
            return outcome;
        }

        this.TryRemoveWorker(worker.Id, out _);
        this.failedWorkerAutoCancel.Forget(worker.Id);
        this.retention.Forget(worker.Id);
        this.concurrency.Forget(worker);
        this.index.Forget(worker);
        this.ForgetIterationStatuses(worker.Id);
        this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
        this.persistence.SynchronizeWorkerState(worker);
        return outcome;
    }

    private void SynchronizeFailedWorkerAutoCancel(WorkerRecord worker)
    {
        if (worker.State == WorkerState.Failed)
        {
            this.failedWorkerAutoCancel.Schedule(worker);
            return;
        }

        this.failedWorkerAutoCancel.Forget(worker.Id);
    }

    private int AutoCancelFailedWorkers(IReadOnlyList<FailedWorkerAutoCancelSchedule> schedules)
    {
        if (schedules.Count == 0)
        {
            return 0;
        }

        var autoCanceledCount = 0;
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            description: "Workable auto-canceled a failed worker after the configured failed-state delay.");
        foreach (var schedule in schedules)
        {
            if (!this.workers.TryGetValue(schedule.WorkerId, out var worker) ||
                !worker.TryAutoCancelFailedWorker(schedule.StateSequence, out var outcome) ||
                outcome is null)
            {
                continue;
            }

            worker.RecordActionHistory(outcome, requestContext);
            this.HandleAcceptedWorkerChange(worker, WorkAction.Cancel);
            this.SynchronizeWorkerIfTracked(worker);
            if (ShouldSignalCurrentCompletion(WorkAction.Cancel))
            {
                worker.SignalCurrentCompletion();
            }

            this.workerEvents.ActionApplied(worker, outcome, requestContext);
            autoCanceledCount++;
        }

        return autoCanceledCount;
    }

    private int PurgeFinalWorkersForRetention(
        IReadOnlyList<WorkerId> workerIds,
        WorkDefinitionId? requiredDefinitionId)
    {
        if (workerIds.Count == 0)
        {
            return 0;
        }

        Dictionary<WorkDefinitionId, (string DefinitionName, List<WorkerId> WorkerIds)>? purgedWorkerIdsByDefinition = null;
        List<WorkerId>? allPurgedWorkerIds = null;
        foreach (var workerId in workerIds)
        {
            if (!this.workers.TryGetValue(workerId, out var worker) ||
                !worker.IsFinal ||
                (requiredDefinitionId is not null && worker.Work.Definition.Id != requiredDefinitionId))
            {
                continue;
            }

            if (!this.TryRemoveWorker(workerId, out var removed))
            {
                continue;
            }

            this.concurrency.Forget(removed);
            this.index.Forget(removed);
            allPurgedWorkerIds ??= [];
            allPurgedWorkerIds.Add(workerId);

            var definition = removed.Work.Definition;
            purgedWorkerIdsByDefinition ??= [];
            if (!purgedWorkerIdsByDefinition.TryGetValue(definition.Id, out var definitionPurge))
            {
                definitionPurge = (definition.Name, []);
                purgedWorkerIdsByDefinition[definition.Id] = definitionPurge;
            }

            definitionPurge.WorkerIds.Add(workerId);
        }

        if (purgedWorkerIdsByDefinition is null)
        {
            return 0;
        }

        if (allPurgedWorkerIds is not null)
        {
            this.ForgetIterationStatuses(allPurgedWorkerIds);
        }

        var purgedCount = 0;
        foreach (var purgedWorkers in purgedWorkerIdsByDefinition)
        {
            purgedCount += purgedWorkers.Value.WorkerIds.Count;
            this.workerEvents.Purged(purgedWorkers.Value.WorkerIds, purgedWorkers.Key, purgedWorkers.Value.DefinitionName);
        }

        return purgedCount;
    }

    private void ForgetIterationStatuses(WorkerId workerId)
    {
        foreach (var reference in this.iterationStatuses.Keys.Where(reference => reference.WorkerId == workerId))
        {
            this.iterationStatuses.TryRemove(reference, out _);
        }
    }

    private void ForgetIterationStatuses(List<WorkerId> workerIds)
    {
        if (workerIds.Count == 0)
        {
            return;
        }

        var workerIdSet = workerIds.Count > 4 ? workerIds.ToHashSet() : null;
        foreach (var reference in this.iterationStatuses.Keys
            .Where(reference => ContainsWorker(workerIds, workerIdSet, reference.WorkerId)))
        {
            this.iterationStatuses.TryRemove(reference, out _);
        }
    }

    private static bool ContainsWorker(
        List<WorkerId> workerIds,
        HashSet<WorkerId>? workerIdSet,
        WorkerId workerId)
        => workerIdSet?.Contains(workerId) ?? workerIds.Contains(workerId);

    private IReadOnlyList<WorkerSnapshot> GetSubjectWorkersLocked(WorkSubjectId subjectId)
        => this.persistence.GetSubjectWorkers(subjectId);

    private IReadOnlyList<WorkerSnapshot> GetSubjectWorkersLocked(WorkDefinitionId definitionId, WorkSubjectId subjectId)
        => this.persistence.GetSubjectWorkers(definitionId, subjectId);

    private static IReadOnlyList<WorkerSnapshot> CreateSnapshotList(IEnumerable<WorkerRecord> workers)
        => [.. workers
            .Select(worker => worker.ToSnapshot())
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

    private Task OnPersistedWorkerMaterialized(
        WorkerPersistenceMaterializedWorker materialized,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var worker = materialized.Worker;
        this.workerEvents.Queued(worker);

        if (materialized.ShouldScheduleStart)
        {
            this.ScheduleStart(worker);
        }

        if (materialized.ShouldDrainQueuedWorkers)
        {
            this.ScheduleConcurrencyDrain(worker.Work.Definition.Id);
        }

        return Task.CompletedTask;
    }

    private bool IsAcceptingWork()
    {
        lock (this.lifecycleSync)
        {
            return this.acceptingWork;
        }
    }

}

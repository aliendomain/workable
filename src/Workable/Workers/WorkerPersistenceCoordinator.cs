using System.Collections.Concurrent;

namespace Workable;

internal interface IWorkerPersistenceCoordinator
{
    Task InitializeAndDrain(IReadOnlyList<WorkDefinition> definitions, CancellationToken cancellationToken);

    void StartBackgroundTasks();

    Task StopBackgroundTasks(CancellationToken cancellationToken);

    Task<WorkerPersistenceQueueAcceptance> AcceptQueuedWorker(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    void SignalAccepted(WorkerRecord worker);

    void SynchronizeWorkerState(WorkerRecord worker);

    Task CompleteDurably(
        WorkerRecord worker,
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken);

    IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkSubjectId subjectId);

    IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkDefinitionId definitionId, WorkSubjectId subjectId);
}

internal sealed class WorkerPersistenceCoordinator : IWorkerPersistenceCoordinator
{
    private readonly Lock sync = new();
    private readonly WorkSystemCatalog catalog;
    private readonly ConcurrentDictionary<WorkerId, WorkerRecord> workers;
    private readonly WorkIdempotencyCoordinator idempotency;
    private readonly WorkConcurrencyCoordinator concurrency;
    private readonly WorkQueueDurabilityCoordinator durability;
    private readonly WorkQueueAcceptanceCoordinator queueAcceptance;
    private readonly Action<WorkerRecord> acceptWorkerIntoMemory;
    private readonly Func<WorkerId, WorkerRecord?> getTrackedWorker;
    private readonly Func<WorkerPersistenceMaterializedWorker, CancellationToken, Task> persistedWorkerMaterialized;

    public WorkerPersistenceCoordinator(
        WorkSystemCatalog catalog,
        ConcurrentDictionary<WorkerId, WorkerRecord> workers,
        WorkerIndex index,
        WorkConcurrencyCoordinator concurrency,
        WorkSystemId workSystemId,
        string? workSystemName,
        IWorkPersistenceStore? persistenceStore,
        Func<bool> isAcceptingWork,
        Func<CancellationToken> getSystemExecutionToken,
        Action<WorkerRecord> acceptWorkerIntoMemory,
        Func<WorkerId, WorkerRecord?> getTrackedWorker,
        Func<WorkerPersistenceMaterializedWorker, CancellationToken, Task> persistedWorkerMaterialized,
        Action<WorkerRecord, WorkInterruptionReason> interruptWorker)
    {
        this.catalog = catalog;
        this.workers = workers;
        this.concurrency = concurrency;
        this.acceptWorkerIntoMemory = acceptWorkerIntoMemory;
        this.getTrackedWorker = getTrackedWorker;
        this.persistedWorkerMaterialized = persistedWorkerMaterialized;
        this.idempotency = new WorkIdempotencyCoordinator(index, workers);
        this.durability = new WorkQueueDurabilityCoordinator(
            persistenceStore,
            workSystemId,
            workSystemName,
            isAcceptingWork,
            getSystemExecutionToken,
            this.AcceptPersistedQueueEntry,
            workerId =>
            {
                if (getTrackedWorker(workerId) is { } worker)
                {
                    interruptWorker(worker, WorkInterruptionReason.LeaseLost);
                }
            });
        this.queueAcceptance = new WorkQueueAcceptanceCoordinator(
            this.idempotency,
            concurrency,
            this.durability);
    }

    public Task InitializeAndDrain(IReadOnlyList<WorkDefinition> definitions, CancellationToken cancellationToken)
        => this.durability.InitializeAndDrain(definitions, cancellationToken);

    public void StartBackgroundTasks()
        => this.durability.StartBackgroundTasks();

    public Task StopBackgroundTasks(CancellationToken cancellationToken)
        => this.durability.StopBackgroundTasks(cancellationToken);

    public async Task<WorkerPersistenceQueueAcceptance> AcceptQueuedWorker(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        PreparedWorkQueueAcceptance acceptance;
        var acceptedIntoMemory = false;

        lock (this.sync)
        {
            acceptance = this.queueAcceptance.Prepare(
                workerId,
                registeredWork,
                input,
                runtimePlan,
                origin,
                now);
            if (acceptance.Outcome.IsAccepted &&
                acceptance.PersistenceRequest is null &&
                acceptance.IdempotencyRequest is null &&
                acceptance.Worker is { } localWorker)
            {
                this.acceptWorkerIntoMemory(localWorker);
                acceptedIntoMemory = true;
            }
        }

        if (!acceptance.Outcome.IsAccepted)
        {
            return WorkerPersistenceQueueAcceptance.Rejected(acceptance.Outcome);
        }

        if (acceptance.PersistenceRequest is { } persistenceRequest)
        {
            var persisted = await this.durability.Enqueue(persistenceRequest, cancellationToken);
            if (persisted.IsAccepted && persistenceRequest.Transaction is null)
            {
                this.durability.SignalReader();
            }

            return persisted.IsAccepted
                ? WorkerPersistenceQueueAcceptance.Durable(
                    persisted,
                    this.durability.CreateHandle(persisted, this.getTrackedWorker))
                : WorkerPersistenceQueueAcceptance.Rejected(persisted);
        }

        var record = acceptance.Worker ?? throw new InvalidOperationException("Accepted in-memory queue operation did not include a worker.");
        if (acceptance.IdempotencyRequest is { } idempotencyRequest)
        {
            var reserved = await this.durability.ReserveIdempotency(idempotencyRequest, cancellationToken);
            if (!reserved.IsAccepted)
            {
                this.concurrency.Forget(record);
                return WorkerPersistenceQueueAcceptance.Rejected(reserved);
            }
        }

        if (!acceptedIntoMemory)
        {
            lock (this.sync)
            {
                this.acceptWorkerIntoMemory(record);
            }
        }

        return WorkerPersistenceQueueAcceptance.InMemory(
            acceptance.Outcome,
            record,
            acceptance.ShouldScheduleStart,
            acceptance.ShouldDrainQueuedWorkers);
    }

    private async Task<WorkerPersistenceMaterializedWorker?> MaterializePersistedQueueEntry(
        WorkQueueDurabilityEntry entry,
        CancellationToken cancellationToken)
    {
        if (this.workers.ContainsKey(entry.Lease.WorkerId))
        {
            return null;
        }

        if (!this.catalog.TryGetWork(entry.DefinitionName, out var registeredWork))
        {
            return null;
        }

        var record = new WorkerRecord(
            entry.Lease.WorkerId,
            registeredWork,
            entry.Input,
            entry.Options,
            entry.Configuration,
            entry.Origin,
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: entry.CreatedAt,
            updatedAt: entry.CreatedAt);

        bool shouldScheduleStart;
        bool shouldDrainQueuedWorkers;
        lock (this.sync)
        {
            if (this.workers.ContainsKey(entry.Lease.WorkerId))
            {
                return null;
            }

            this.acceptWorkerIntoMemory(record);

            shouldScheduleStart = entry.Configuration.Start.Policy != WorkStartPolicy.DoNotStart;
            shouldDrainQueuedWorkers = entry.Configuration.Concurrency.IsEnabled && shouldScheduleStart;
            if (entry.Configuration.Concurrency.IsEnabled && shouldScheduleStart)
            {
                var reservation = this.concurrency.QueueExistingWorkerForStart(record);
                shouldScheduleStart = reservation == WorkConcurrencyReservationStatus.Reserved;
            }
        }

        this.durability.TrackLease(record.Id, entry.Lease);

        return new WorkerPersistenceMaterializedWorker(
            record,
            shouldScheduleStart,
            shouldDrainQueuedWorkers);
    }

    public void SignalAccepted(WorkerRecord worker)
        => this.durability.SignalAccepted(worker);

    public void SynchronizeWorkerState(WorkerRecord worker)
    {
        if (worker.State is WorkerState.Completed or WorkerState.Canceled)
        {
            this.durability.DeleteFinal(worker.Id);
            return;
        }

        if (worker.State is WorkerState.Failed)
        {
            this.durability.RetainFailed(worker.Id);
        }
    }

    public Task CompleteDurably(
        WorkerRecord worker,
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!worker.Configuration.QueueDurability.CompleteDurably)
        {
            throw new InvalidOperationException(
                "Durable completion is not enabled for this worker. Configure the work with CompleteDurably before calling IWorkExecutionContext.CompleteDurably.");
        }

        return this.durability.CompleteDurably(worker.Id, transaction, cancellationToken);
    }

    public IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkSubjectId subjectId)
        => this.idempotency.GetSubjectWorkers(subjectId);

    public IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkDefinitionId definitionId, WorkSubjectId subjectId)
        => this.idempotency.GetSubjectWorkers(definitionId, subjectId);

    private async Task AcceptPersistedQueueEntry(
        WorkQueueDurabilityEntry entry,
        CancellationToken cancellationToken)
    {
        var materialized = await this.MaterializePersistedQueueEntry(entry, cancellationToken);
        if (materialized is not null)
        {
            await this.persistedWorkerMaterialized(materialized, cancellationToken);
        }
    }
}

internal sealed record WorkerPersistenceQueueAcceptance(
    WorkQueueOutcome Outcome,
    WorkerRecord? Worker,
    IWorkerHandle? Handle,
    bool ShouldScheduleStart,
    bool ShouldDrainQueuedWorkers)
{
    public static WorkerPersistenceQueueAcceptance Rejected(WorkQueueOutcome outcome)
        => new(outcome, Worker: null, Handle: null, ShouldScheduleStart: false, ShouldDrainQueuedWorkers: false);

    public static WorkerPersistenceQueueAcceptance Durable(WorkQueueOutcome outcome, IWorkerHandle handle)
        => new(outcome, Worker: null, handle, ShouldScheduleStart: false, ShouldDrainQueuedWorkers: false);

    public static WorkerPersistenceQueueAcceptance InMemory(
        WorkQueueOutcome outcome,
        WorkerRecord worker,
        bool shouldScheduleStart,
        bool shouldDrainQueuedWorkers)
        => new(outcome, worker, Handle: null, shouldScheduleStart, shouldDrainQueuedWorkers);
}

internal sealed record WorkerPersistenceMaterializedWorker(
    WorkerRecord Worker,
    bool ShouldScheduleStart,
    bool ShouldDrainQueuedWorkers);

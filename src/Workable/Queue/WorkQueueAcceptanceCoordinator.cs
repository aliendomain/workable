namespace Workable;

internal sealed class WorkQueueAcceptanceCoordinator(
    WorkIdempotencyCoordinator idempotency,
    WorkConcurrencyCoordinator concurrency,
    WorkQueueDurabilityCoordinator durability)
{
    public PreparedWorkQueueAcceptance Prepare(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        DateTimeOffset now)
    {
        var idempotencyErrors = idempotency.Validate(
            registeredWork.Definition.Id,
            input?.SubjectId,
            runtimePlan.Configuration.Idempotency,
            includeActiveWorkerConflicts: runtimePlan.Configuration.Idempotency.Storage == WorkIdempotencyStorage.Local);
        if (idempotencyErrors.Count > 0)
        {
            return PreparedWorkQueueAcceptance.Rejected(WorkQueueOutcome.Invalid(
                registeredWork.Definition.Id,
                idempotencyErrors));
        }

        return runtimePlan.Configuration.QueueDurability.IsEnabled
            ? this.PreparePersistent(workerId, registeredWork, input, runtimePlan, origin, now)
            : this.PrepareInMemory(workerId, registeredWork, input, runtimePlan, origin, now);
    }

    private PreparedWorkQueueAcceptance PrepareInMemory(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        DateTimeOffset now)
    {
        if (runtimePlan.Configuration.Idempotency is { IsEnabled: true, Storage: WorkIdempotencyStorage.Persistence } &&
            runtimePlan.Options.QueueDurabilityTransaction is not null)
        {
            return PreparedWorkQueueAcceptance.Rejected(WorkQueueOutcome.Invalid(
                registeredWork.Definition.Id,
                [WorkMessage.Error(
                    "workable.idempotency.persistence_transaction_requires_durable_queue",
                    "Caller-owned persistence transactions require durable queueing so Workable can wait until the transaction commits before materializing the worker.",
                    "options.queueDurabilityTransaction")]));
        }

        var shouldStart = runtimePlan.ShouldStart;
        if (runtimePlan.Configuration.Concurrency.IsEnabled && shouldStart)
        {
            var reservation = concurrency.QueueWorker(
                registeredWork.Definition.Id,
                input,
                runtimePlan.Configuration.Concurrency,
                status =>
                    CreateQueuedWorker(
                        workerId,
                        registeredWork,
                        input,
                        runtimePlan,
                        origin,
                        isStartDeferred: status == WorkConcurrencyReservationStatus.Deferred,
                        now));
            if (reservation.Status == WorkConcurrencyReservationStatus.Rejected)
            {
                return PreparedWorkQueueAcceptance.Rejected(WorkQueueOutcome.Invalid(
                    registeredWork.Definition.Id,
                    [WorkMessage.Info("workable.concurrency.capacity_reached", "Concurrency capacity has been reached for this work group.", "configuration.concurrency.maximumCapacity")]));
            }

            var reservedWorker = reservation.Worker ?? throw new InvalidOperationException("Accepted concurrency queue reservation did not include a worker.");
            var outcome = WorkQueueOutcome.Accepted(
                registeredWork.Definition.Id,
                workerId,
                reservation.Status == WorkConcurrencyReservationStatus.Deferred
                    ? [WorkMessage.Info("workable.concurrency.start_deferred", "Worker start was deferred until concurrency capacity is available.", "configuration.concurrency")]
                    : null);
            return PreparedWorkQueueAcceptance.InMemory(
                outcome,
                reservedWorker,
                this.CreateIdempotencyRequest(workerId, registeredWork, input, runtimePlan, origin, now),
                shouldScheduleStart: reservation.Status == WorkConcurrencyReservationStatus.Reserved,
                shouldDrainQueuedWorkers: false);
        }

        var record = CreateQueuedWorker(
            workerId,
            registeredWork,
            input,
            runtimePlan,
            origin,
            isStartDeferred: false,
            now);

        return PreparedWorkQueueAcceptance.InMemory(
            WorkQueueOutcome.Accepted(registeredWork.Definition.Id, workerId),
            record,
            this.CreateIdempotencyRequest(workerId, registeredWork, input, runtimePlan, origin, now),
            shouldScheduleStart: shouldStart,
            shouldDrainQueuedWorkers: runtimePlan.Configuration.Concurrency.IsEnabled && shouldStart);
    }

    private PreparedWorkQueueAcceptance PreparePersistent(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        DateTimeOffset now)
    {
        var idempotencyRequest = runtimePlan.Configuration.Idempotency is { IsEnabled: true, Storage: WorkIdempotencyStorage.Persistence }
            ? new WorkQueueDurabilityIdempotency(input?.SubjectId ?? throw new InvalidOperationException("Persistent idempotent queue acceptance requires a subject id."))
            : null;

        return PreparedWorkQueueAcceptance.Persistent(durability.CreateRequest(
            workerId,
            registeredWork,
            input,
            runtimePlan.Options,
            runtimePlan.Configuration,
            origin,
            now,
            idempotencyRequest));
    }

    private WorkIdempotencyPersistenceRequest? CreateIdempotencyRequest(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        DateTimeOffset now)
        => runtimePlan.Configuration.Idempotency is { IsEnabled: true, Storage: WorkIdempotencyStorage.Persistence }
            ? durability.CreateIdempotencyRequest(
                workerId,
                registeredWork,
                input?.SubjectId ?? throw new InvalidOperationException("Persistent idempotent queue acceptance requires a subject id."),
                runtimePlan.Options,
                origin,
                now)
            : null;

    private static WorkerRecord CreateQueuedWorker(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkOrigin origin,
        bool isStartDeferred,
        DateTimeOffset now)
        => new(
            workerId,
            registeredWork,
            input,
            runtimePlan.Options,
            runtimePlan.Configuration,
            origin,
            WorkerState.Queued,
            isStartDeferred,
            messages: [],
            createdAt: now,
            updatedAt: now);
}

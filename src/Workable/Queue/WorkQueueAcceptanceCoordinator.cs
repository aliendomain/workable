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
        WorkRequestContext requestContext,
        DateTimeOffset now)
    {
        var idempotencyErrors = idempotency.Validate(
            registeredWork.Definition.Id,
            input?.SubjectId,
            runtimePlan.Configuration.Coordination.Idempotency,
            includeActiveWorkerConflicts: runtimePlan.Configuration.Coordination.IsIdempotencyEnabled &&
                runtimePlan.Configuration.Coordination.Storage == WorkCoordinationStorage.Local);
        if (idempotencyErrors.Count > 0)
        {
            return PreparedWorkQueueAcceptance.Rejected(WorkQueueOutcome.Invalid(
                registeredWork.Definition.Id,
                idempotencyErrors));
        }

        return runtimePlan.Configuration.Coordination.IsDurabilityEnabled
            ? this.PreparePersistent(workerId, registeredWork, input, runtimePlan, requestContext, now)
            : this.PrepareInMemory(workerId, registeredWork, input, runtimePlan, requestContext, now);
    }

    private PreparedWorkQueueAcceptance PrepareInMemory(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkRequestContext requestContext,
        DateTimeOffset now)
    {
        if (runtimePlan.Configuration.Coordination.IsPersistentIdempotencyEnabled &&
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
        if (runtimePlan.Configuration.Coordination.IsConcurrencyEnabled && shouldStart)
        {
            var reservation = concurrency.QueueWorker(
                registeredWork.Definition.Id,
                input,
                runtimePlan.Configuration.Coordination.Concurrency,
                status =>
                    CreateQueuedWorker(
                        workerId,
                        registeredWork,
                        input,
                        runtimePlan,
                        requestContext,
                        isStartDeferred: status == WorkConcurrencyReservationStatus.Deferred,
                        now));
            if (reservation.Status == WorkConcurrencyReservationStatus.Rejected)
            {
                return PreparedWorkQueueAcceptance.Rejected(WorkQueueOutcome.Invalid(
                    registeredWork.Definition.Id,
                    [WorkMessage.Info("workable.concurrency.capacity_reached", "Concurrency capacity has been reached for this work group.", "configuration.coordination.concurrency.maximumCapacity")]));
            }

            var reservedWorker = reservation.Worker ?? throw new InvalidOperationException("Accepted concurrency queue reservation did not include a worker.");
            var outcome = WorkQueueOutcome.Accepted(
                registeredWork.Definition.Id,
                workerId,
                reservation.Status == WorkConcurrencyReservationStatus.Deferred
                    ? [WorkMessage.Info("workable.concurrency.start_deferred", "Worker start was deferred until concurrency capacity is available.", "configuration.coordination.concurrency")]
                    : null);
            return PreparedWorkQueueAcceptance.InMemory(
                outcome,
                reservedWorker,
                this.CreateIdempotencyRequest(workerId, registeredWork, input, runtimePlan, requestContext, now),
                shouldScheduleStart: reservation.Status == WorkConcurrencyReservationStatus.Reserved,
                shouldDrainQueuedWorkers: false);
        }

        var record = CreateQueuedWorker(
            workerId,
            registeredWork,
            input,
            runtimePlan,
            requestContext,
            isStartDeferred: false,
            now);

        return PreparedWorkQueueAcceptance.InMemory(
            WorkQueueOutcome.Accepted(registeredWork.Definition.Id, workerId),
            record,
            this.CreateIdempotencyRequest(workerId, registeredWork, input, runtimePlan, requestContext, now),
            shouldScheduleStart: shouldStart,
            shouldDrainQueuedWorkers: runtimePlan.Configuration.Coordination.IsConcurrencyEnabled && shouldStart);
    }

    private PreparedWorkQueueAcceptance PreparePersistent(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkRequestContext requestContext,
        DateTimeOffset now)
    {
        var idempotencyRequest = runtimePlan.Configuration.Coordination.IsPersistentIdempotencyEnabled
            ? new WorkQueueDurabilityIdempotency(input?.SubjectId ?? throw new InvalidOperationException("Persistent idempotent queue acceptance requires a subject id."))
            : null;

        return PreparedWorkQueueAcceptance.Persistent(durability.CreateRequest(
            workerId,
            registeredWork,
            input,
            runtimePlan.Options,
            runtimePlan.Configuration,
            requestContext,
            now,
            idempotencyRequest));
    }

    private WorkIdempotencyPersistenceRequest? CreateIdempotencyRequest(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkRequestContext requestContext,
        DateTimeOffset now)
        => runtimePlan.Configuration.Coordination.IsPersistentIdempotencyEnabled
            ? durability.CreateIdempotencyRequest(
                workerId,
                registeredWork,
                input?.SubjectId ?? throw new InvalidOperationException("Persistent idempotent queue acceptance requires a subject id."),
                runtimePlan.Options,
                requestContext,
                now)
            : null;

    private static WorkerRecord CreateQueuedWorker(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        RegisteredWorkRuntimePlan runtimePlan,
        WorkRequestContext requestContext,
        bool isStartDeferred,
        DateTimeOffset now)
        => new(
            workerId,
            registeredWork,
            input,
            runtimePlan.Options,
            runtimePlan.Configuration,
            requestContext,
            WorkerState.Queued,
            isStartDeferred,
            messages: [],
            createdAt: now,
            updatedAt: now);
}

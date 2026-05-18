namespace Workable;

internal sealed record PreparedWorkQueueAcceptance(
    WorkQueueOutcome Outcome,
    WorkerRecord? Worker,
    WorkQueueDurabilityEnqueueRequest? PersistenceRequest,
    WorkIdempotencyPersistenceRequest? IdempotencyRequest,
    bool ShouldScheduleStart,
    bool ShouldDrainQueuedWorkers)
{
    public static PreparedWorkQueueAcceptance Rejected(WorkQueueOutcome outcome)
        => new(outcome, null, null, null, false, false);

    public static PreparedWorkQueueAcceptance InMemory(
        WorkQueueOutcome outcome,
        WorkerRecord worker,
        WorkIdempotencyPersistenceRequest? idempotencyRequest,
        bool shouldScheduleStart,
        bool shouldDrainQueuedWorkers)
        => new(outcome, worker, null, idempotencyRequest, shouldScheduleStart, shouldDrainQueuedWorkers);

    public static PreparedWorkQueueAcceptance Persistent(WorkQueueDurabilityEnqueueRequest request)
        => new(WorkQueueOutcome.Accepted(request.Definition.Id, request.WorkerId), null, request, null, false, false);
}

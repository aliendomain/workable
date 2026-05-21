namespace Workable;
public sealed record WorkActionOutcome(
    WorkActionStatus Status,
    WorkAction Action,
    WorkerId? WorkerId,
    WorkerSnapshot? Worker,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkActionStatus.Accepted;

    public static WorkActionOutcome Accepted(WorkAction action, WorkerSnapshot worker, IEnumerable<WorkMessage>? messages = null)
        => new(WorkActionStatus.Accepted, action, worker.Id, worker, [.. messages ?? []]);

    public static WorkActionOutcome NotFound(WorkAction action, WorkerId workerId)
        => new(WorkActionStatus.NotFound, action, workerId, null, [WorkMessage.Error("workable.worker.not_found", $"No worker was found for '{workerId}'.", "worker")]);

    public static WorkActionOutcome Unauthorized(WorkAction action, WorkerId workerId)
        => new(
            WorkActionStatus.Unauthorized,
            action,
            workerId,
            null,
            [WorkMessage.Error("workable.worker.unauthorized", $"You are not authorized to operate worker '{workerId}'.", "worker.authorization")]);

    public static WorkActionOutcome Invalid(WorkAction action, WorkerSnapshot? worker, IEnumerable<WorkMessage> messages)
        => new(WorkActionStatus.Invalid, action, worker?.Id, worker, [.. messages]);

    public static WorkActionOutcome Conflict(WorkAction action, WorkerSnapshot worker, IEnumerable<WorkMessage> messages)
        => new(WorkActionStatus.Conflict, action, worker.Id, worker, [.. messages]);
}

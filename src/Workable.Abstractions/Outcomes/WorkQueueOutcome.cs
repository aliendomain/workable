namespace Workable;
public sealed record WorkQueueOutcome(
    WorkQueueStatus Status,
    WorkerId? WorkerId,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkQueueStatus.Accepted;

    public static WorkQueueOutcome Accepted(WorkerId workerId, IEnumerable<WorkMessage>? messages = null)
        => new(WorkQueueStatus.Accepted, workerId, [.. messages ?? []]);

    public static WorkQueueOutcome NotFound(string target)
        => new(WorkQueueStatus.NotFound, null, [WorkMessage.Error("workable.definition.not_found", $"No work definition was found for '{target}'.", "definition")]);

    public static WorkQueueOutcome Unauthorized(string target)
        => new(
            WorkQueueStatus.Unauthorized,
            null,
            [WorkMessage.Error("workable.definition.unauthorized", $"You are not authorized to queue work '{target}'.", "definition.authorization")]);

    public static WorkQueueOutcome Invalid(IEnumerable<WorkMessage> messages)
        => new(WorkQueueStatus.Invalid, null, [.. messages]);
}

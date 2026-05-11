namespace Workable;
public sealed record WorkQueueOutcome(
    WorkQueueStatus Status,
    WorkDefinitionId? DefinitionId,
    WorkerId? WorkerId,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkQueueStatus.Accepted;

    public static WorkQueueOutcome Accepted(WorkDefinitionId definitionId, WorkerId workerId, IEnumerable<WorkMessage>? messages = null)
        => new(WorkQueueStatus.Accepted, definitionId, workerId, [.. messages ?? []]);

    public static WorkQueueOutcome NotFound(string target)
        => new(WorkQueueStatus.NotFound, null, null, [WorkMessage.Error("workable.definition.not_found", $"No work definition was found for '{target}'.", "definition")]);

    public static WorkQueueOutcome Invalid(WorkDefinitionId? definitionId, IEnumerable<WorkMessage> messages)
        => new(WorkQueueStatus.Invalid, definitionId, null, [.. messages]);
}

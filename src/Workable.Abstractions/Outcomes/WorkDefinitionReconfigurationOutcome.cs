namespace Workable;

public sealed record WorkDefinitionReconfigurationOutcome(
    WorkDefinitionReconfigurationStatus Status,
    WorkDefinitionId DefinitionId,
    WorkDefinition? Definition,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkDefinitionReconfigurationStatus.Accepted;

    public static WorkDefinitionReconfigurationOutcome Accepted(WorkDefinition definition, IEnumerable<WorkMessage>? messages = null)
        => new(WorkDefinitionReconfigurationStatus.Accepted, definition.Id, definition, [.. messages ?? []]);

    public static WorkDefinitionReconfigurationOutcome NotFound(WorkDefinitionId definitionId)
        => new(
            WorkDefinitionReconfigurationStatus.NotFound,
            definitionId,
            null,
            [WorkMessage.Error("workable.definition.not_found", $"No work definition was found for '{definitionId}'.", "definition")]);

    public static WorkDefinitionReconfigurationOutcome Invalid(WorkDefinition definition, IEnumerable<WorkMessage> messages)
        => new(WorkDefinitionReconfigurationStatus.Invalid, definition.Id, definition, [.. messages]);

    public static WorkDefinitionReconfigurationOutcome Conflict(WorkDefinition definition, long expectedRevision)
        => new(
            WorkDefinitionReconfigurationStatus.Conflict,
            definition.Id,
            definition,
            [WorkMessage.Error(
                "workable.definition.revision_conflict",
                $"Work definition '{definition.Name}' is at revision {definition.Revision}, but revision {expectedRevision} was supplied.",
                "definition.revision")]);
}

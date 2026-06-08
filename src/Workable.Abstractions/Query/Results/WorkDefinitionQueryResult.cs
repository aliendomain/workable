namespace Workable;

/// <summary>
/// Represents a list-style result that returns registered work definitions.
/// </summary>
/// <param name="Definitions">The matching definitions in the current result set.</param>
public sealed record WorkDefinitionQueryResult(IReadOnlyList<WorkDefinition> Definitions) :
    WorkQueryListResult<WorkDefinition>(Definitions);

namespace Workable;

public sealed record WorkDefinitionQueryResult(IReadOnlyList<WorkDefinition> Definitions) :
    WorkQueryListResult<WorkDefinition>(Definitions);

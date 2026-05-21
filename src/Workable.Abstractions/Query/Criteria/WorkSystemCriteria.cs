namespace Workable;

public sealed record WorkSystemCriteria(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    string? Category = null,
    bool IncludeSubcategories = true,
    bool IncludeThroughput = false,
    IReadOnlySet<WorkDefinitionId>? DefinitionIds = null);

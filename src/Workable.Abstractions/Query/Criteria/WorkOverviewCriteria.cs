namespace Workable;

public sealed record WorkOverviewCriteria(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    string? Category = null,
    bool IncludeSubcategories = true,
    bool IncludeThroughput = false);

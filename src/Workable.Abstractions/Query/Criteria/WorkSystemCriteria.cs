namespace Workable;

public sealed record WorkSystemCriteria(
    string? DefinitionName = null,
    IReadOnlySet<string>? DefinitionNames = null,
    string? Category = null,
    bool IncludeSubcategories = true,
    bool IncludeThroughput = false);

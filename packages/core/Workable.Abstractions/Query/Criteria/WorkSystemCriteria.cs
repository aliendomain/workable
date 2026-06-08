namespace Workable;

/// <summary>
/// Scopes whole-system summary queries to a subset of definitions.
/// </summary>
/// <param name="DefinitionName">An optional exact definition name filter.</param>
/// <param name="DefinitionNames">Optional exact definition names to include.</param>
/// <param name="Category">An optional category-path filter.</param>
/// <param name="IncludeSubcategories">Whether a category filter should include descendant category paths.</param>
/// <param name="IncludeThroughput">Whether the caller intends to include throughput data in the resulting system snapshot.</param>
public sealed record WorkSystemCriteria(
    string? DefinitionName = null,
    IReadOnlySet<string>? DefinitionNames = null,
    string? Category = null,
    bool IncludeSubcategories = true,
    bool IncludeThroughput = false);

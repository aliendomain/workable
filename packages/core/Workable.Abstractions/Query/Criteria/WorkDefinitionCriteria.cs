namespace Workable;

/// <summary>
/// Filters definition-browsing queries.
/// </summary>
/// <param name="Name">An optional exact definition name filter.</param>
/// <param name="Names">Optional exact definition names to include.</param>
/// <param name="Category">An optional category-path filter.</param>
/// <param name="Search">Optional free-text search text applied to definition metadata fields.</param>
/// <param name="IncludeSubcategories">Whether a category filter should include descendant category paths.</param>
public sealed record WorkDefinitionCriteria(
    string? Name = null,
    IReadOnlySet<string>? Names = null,
    string? Category = null,
    string? Search = null,
    bool IncludeSubcategories = true);

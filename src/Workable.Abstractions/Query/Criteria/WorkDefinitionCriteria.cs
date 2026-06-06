namespace Workable;

public sealed record WorkDefinitionCriteria(
    string? Name = null,
    IReadOnlySet<string>? Names = null,
    string? Category = null,
    string? Search = null,
    bool IncludeSubcategories = true);

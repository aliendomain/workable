namespace Workable;

public sealed record WorkDefinitionQuery(
    WorkDefinitionId? Id = null,
    string? Name = null,
    string? Category = null,
    string? Search = null,
    bool IncludeSubcategories = true);

namespace Workable;

/// <summary>
/// Represents a list-style result that returns iteration key-type facets.
/// </summary>
/// <param name="KeyTypes">The matching key-type facets.</param>
public sealed record WorkIterationKeyTypeFacetQueryResult(IReadOnlyList<WorkIterationKeyTypeFacet> KeyTypes) :
    WorkQueryListResult<WorkIterationKeyTypeFacet>(KeyTypes);

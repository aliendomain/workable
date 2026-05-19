namespace Workable;

public sealed record WorkIterationKeyTypeFacetQueryResult(IReadOnlyList<WorkIterationKeyTypeFacet> KeyTypes) :
    WorkQueryListResult<WorkIterationKeyTypeFacet>(KeyTypes);

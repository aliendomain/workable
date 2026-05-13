namespace Workable;

public sealed record WorkIterationKeyTypeFacet(
    string Type,
    int IterationCount,
    IReadOnlyDictionary<WorkKeyKind, int> IterationCountByKind);

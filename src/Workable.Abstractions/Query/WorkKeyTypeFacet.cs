namespace Workable;

public sealed record WorkKeyTypeFacet(
    string Type,
    int WorkerCount,
    IReadOnlyDictionary<WorkKeyKind, int> WorkerCountByKind);

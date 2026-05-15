namespace Workable;

public sealed record WorkIterationKeyTypeDescriptor(
    string Type,
    int IterationCount,
    IReadOnlyDictionary<WorkKeyKind, int> IterationCountByKind,
    IReadOnlyList<WorkerIterationOverviewItem> Iterations);

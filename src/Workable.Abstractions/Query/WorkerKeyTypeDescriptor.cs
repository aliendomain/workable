namespace Workable;

public sealed record WorkerKeyTypeDescriptor(
    string Type,
    int WorkerCount,
    IReadOnlyDictionary<WorkKeyKind, int> WorkerCountByKind,
    IReadOnlyList<WorkerOverviewItem> Workers);

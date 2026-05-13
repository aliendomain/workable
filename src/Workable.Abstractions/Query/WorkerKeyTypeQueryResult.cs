namespace Workable;

public sealed record WorkerKeyTypeQueryResult(
    IReadOnlyList<WorkerKeyTypeDescriptor> Types,
    int TotalCount,
    int Skip,
    int Take);

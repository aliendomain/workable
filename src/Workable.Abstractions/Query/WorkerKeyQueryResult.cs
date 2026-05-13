namespace Workable;

public sealed record WorkerKeyQueryResult(
    IReadOnlyList<WorkerKeyDescriptor> Keys,
    int TotalCount,
    int Skip,
    int Take);

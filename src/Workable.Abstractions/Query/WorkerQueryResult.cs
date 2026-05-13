namespace Workable;

public sealed record WorkerQueryResult(
    IReadOnlyList<WorkerOverviewItem> Workers,
    int TotalCount,
    int Skip,
    int Take);

namespace Workable;

public sealed record WorkerQueryResult(
    IReadOnlyList<WorkerSummary> Workers,
    int TotalCount,
    int Skip,
    int Take);

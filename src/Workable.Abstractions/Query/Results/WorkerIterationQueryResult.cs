namespace Workable;

public sealed record WorkerIterationQueryResult(
    IReadOnlyList<WorkerIterationOverviewItem> Iterations,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

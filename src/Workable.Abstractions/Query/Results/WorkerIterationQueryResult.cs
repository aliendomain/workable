namespace Workable;

/// <summary>
/// Represents one page of worker-iteration overview rows.
/// </summary>
/// <param name="Iterations">The iteration overview rows in the current page.</param>
/// <param name="TotalCount">The total number of matching rows before paging is applied.</param>
/// <param name="Skip">The number of matching rows skipped before this page.</param>
/// <param name="Take">The requested page size after Workable applied its limits.</param>
public sealed record WorkerIterationQueryResult(
    IReadOnlyList<WorkerIterationOverviewItem> Iterations,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

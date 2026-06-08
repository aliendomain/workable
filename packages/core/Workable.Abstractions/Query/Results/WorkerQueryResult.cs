namespace Workable;

/// <summary>
/// Represents one page of worker overview rows.
/// </summary>
/// <param name="Workers">The worker overview rows in the current page.</param>
/// <param name="TotalCount">The total number of matching rows before paging is applied.</param>
/// <param name="Skip">The number of matching rows skipped before this page.</param>
/// <param name="Take">The requested page size after Workable applied its limits.</param>
public sealed record WorkerQueryResult(
    IReadOnlyList<WorkerOverviewItem> Workers,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

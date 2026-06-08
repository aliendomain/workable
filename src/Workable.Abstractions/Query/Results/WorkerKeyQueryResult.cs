namespace Workable;

/// <summary>
/// Represents one page of grouped worker-key results.
/// </summary>
/// <param name="Keys">The grouped worker keys in the current page.</param>
/// <param name="TotalCount">The total number of matching grouped keys before paging is applied.</param>
/// <param name="Skip">The number of matching grouped keys skipped before this page.</param>
/// <param name="Take">The requested page size after Workable applied its limits.</param>
public sealed record WorkerKeyQueryResult(
    IReadOnlyList<WorkerKeyDescriptor> Keys,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

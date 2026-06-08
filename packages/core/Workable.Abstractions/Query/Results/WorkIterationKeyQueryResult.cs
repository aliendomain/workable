namespace Workable;

/// <summary>
/// Represents one page of grouped iteration-key results.
/// </summary>
/// <param name="Keys">The grouped iteration keys in the current page.</param>
/// <param name="TotalCount">The total number of matching grouped keys before paging is applied.</param>
/// <param name="Skip">The number of matching grouped keys skipped before this page.</param>
/// <param name="Take">The requested page size after Workable applied its limits.</param>
public sealed record WorkIterationKeyQueryResult(
    IReadOnlyList<WorkIterationKeyDescriptor> Keys,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

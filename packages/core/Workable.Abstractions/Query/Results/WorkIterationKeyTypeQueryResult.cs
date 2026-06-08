namespace Workable;

/// <summary>
/// Represents one page of grouped iteration key-type results.
/// </summary>
/// <param name="Types">The grouped key types in the current page.</param>
/// <param name="TotalCount">The total number of matching grouped key types before paging is applied.</param>
/// <param name="Skip">The number of matching grouped key types skipped before this page.</param>
/// <param name="Take">The requested page size after Workable applied its limits.</param>
public sealed record WorkIterationKeyTypeQueryResult(
    IReadOnlyList<WorkIterationKeyTypeDescriptor> Types,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

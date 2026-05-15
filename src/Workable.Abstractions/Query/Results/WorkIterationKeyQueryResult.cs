namespace Workable;

public sealed record WorkIterationKeyQueryResult(
    IReadOnlyList<WorkIterationKeyDescriptor> Keys,
    int TotalCount,
    int Skip,
    int Take) : IWorkQueryResult;

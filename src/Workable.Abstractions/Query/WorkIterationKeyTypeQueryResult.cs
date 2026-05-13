namespace Workable;

public sealed record WorkIterationKeyTypeQueryResult(
    IReadOnlyList<WorkIterationKeyTypeDescriptor> Types,
    int TotalCount,
    int Skip,
    int Take);

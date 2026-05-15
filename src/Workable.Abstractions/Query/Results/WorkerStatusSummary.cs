namespace Workable;

public sealed record WorkerStatusSummary(
    int Total,
    int Active,
    int Final,
    IReadOnlyDictionary<WorkerState, int> Counts) : IWorkQueryResult;

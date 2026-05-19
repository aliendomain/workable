namespace Workable;

public sealed record WorkerIterationOverviewQueryResult(IReadOnlyList<WorkerIterationOverviewItem> Iterations) :
    WorkQueryListResult<WorkerIterationOverviewItem>(Iterations);

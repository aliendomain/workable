namespace Workable;

public sealed record WorkSystemFailedWorkersOverview(
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers) : IWorkQueryResult;

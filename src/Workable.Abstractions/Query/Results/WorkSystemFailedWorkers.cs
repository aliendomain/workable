namespace Workable;

public sealed record WorkSystemFailedWorkers(
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers) : IWorkQueryResult;

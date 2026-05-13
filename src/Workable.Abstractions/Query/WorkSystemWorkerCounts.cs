namespace Workable;

public sealed record WorkSystemWorkerCounts(
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState);

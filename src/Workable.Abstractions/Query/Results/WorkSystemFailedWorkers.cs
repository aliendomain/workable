namespace Workable;

/// <summary>
/// Represents a compact summary of failed workers in a scoped system slice.
/// </summary>
/// <param name="ActiveWorkerCount">The number of active workers in the scoped system slice.</param>
/// <param name="FinalWorkerCount">The number of final-state workers in the scoped system slice.</param>
/// <param name="FailedWorkerCount">The number of failed workers in the scoped system slice.</param>
/// <param name="WorkerCountByState">Worker counts grouped by worker state.</param>
/// <param name="FailedWorkers">The failed worker overview rows returned for the query.</param>
public sealed record WorkSystemFailedWorkers(
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers) : IWorkQueryResult;

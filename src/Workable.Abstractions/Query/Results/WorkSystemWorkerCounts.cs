namespace Workable;

/// <summary>
/// Represents a compact worker-count summary for a scoped system slice.
/// </summary>
/// <param name="DefinitionCount">The number of definitions in the scoped system slice.</param>
/// <param name="ActiveWorkerCount">The number of active workers in the scoped system slice.</param>
/// <param name="FinalWorkerCount">The number of final-state workers in the scoped system slice.</param>
/// <param name="FailedWorkerCount">The number of failed workers in the scoped system slice.</param>
/// <param name="WorkerCountByState">Worker counts grouped by worker state.</param>
/// <param name="OldestQueuedAt">The oldest queued worker creation time, when one exists.</param>
public sealed record WorkSystemWorkerCounts(
    int DefinitionCount,
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    DateTimeOffset? OldestQueuedAt) : IWorkQueryResult;

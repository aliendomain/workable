namespace Workable;

/// <summary>
/// Identifies one optimistic-concurrency version of a worker.
/// </summary>
/// <param name="WorkerId">The worker identifier.</param>
/// <param name="Revision">The expected worker revision.</param>
public readonly record struct WorkerVersion(WorkerId WorkerId, long Revision);

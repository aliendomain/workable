namespace Workable;

/// <summary>
/// Identifies one iteration within a specific worker.
/// </summary>
/// <param name="WorkerId">The identifier of the owning worker.</param>
/// <param name="Sequence">The monotonic sequence number of the iteration within the worker.</param>
public readonly record struct WorkerIterationReference(WorkerId WorkerId, long Sequence);

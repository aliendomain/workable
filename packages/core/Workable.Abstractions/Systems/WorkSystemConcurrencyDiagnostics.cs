namespace Workable;

/// <summary>
/// Describes workers that were accepted but deferred from starting because of concurrency limits.
/// </summary>
/// <param name="DeferredStartCount">The number of workers currently waiting for concurrency capacity.</param>
/// <param name="OldestDeferredStartAge">How long the oldest deferred worker has been waiting.</param>
/// <param name="LastDrainReleasedCount">The number of deferred workers released by the most recent concurrency drain.</param>
public sealed record WorkSystemConcurrencyDiagnostics(
    int DeferredStartCount,
    TimeSpan OldestDeferredStartAge,
    int LastDrainReleasedCount);

namespace Workable;

/// <summary>
/// Represents the outcome of stopping a system.
/// </summary>
/// <param name="ForceInterruptedWorkers">The authoritative worker snapshots that had to be force interrupted.</param>
public sealed record WorkSystemStopResult(
    IReadOnlyList<WorkerSnapshot> ForceInterruptedWorkers)
{
    /// <summary>
    /// Gets the worker snapshots whose cancellation was requested during shutdown.
    /// </summary>
    public IReadOnlyList<WorkerSnapshot> CancellationRequestedWorkers { get; init; } = [];

    /// <summary>
    /// Gets compact summary rows for workers whose cancellation was requested during shutdown.
    /// </summary>
    public IReadOnlyList<WorkSystemShutdownWorker> CancellationRequestedWorkerSummaries { get; init; } = [];

    /// <summary>
    /// Gets compact summary rows for workers that had to be force interrupted.
    /// </summary>
    public IReadOnlyList<WorkSystemShutdownWorker> ForceInterruptedWorkerSummaries { get; init; } = [];

    /// <summary>
    /// Gets the definition names of workers that had to be force interrupted.
    /// </summary>
    public IReadOnlyList<string> ForceInterruptedWorkerNames
        => [.. this.ForceInterruptedWorkerSummaries.Select(worker => worker.DefinitionName)];

    /// <summary>
    /// Gets the grace period Workable allowed before force interruption was applied.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; init; }
}

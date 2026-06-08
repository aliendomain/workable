namespace Workable;

/// <summary>
/// Represents the HTTP response returned after stopping a system.
/// </summary>
/// <param name="Name">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
/// <param name="State">The resulting lifecycle state of the system.</param>
/// <param name="ForceInterruptedWorkers">The authoritative worker snapshots that had to be force interrupted.</param>
public sealed record WorkableHttpSystemStopResult(
    string? Name,
    WorkSystemState State,
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
    public IReadOnlyList<string> ForceInterruptedWorkerNames { get; init; } = [];

    /// <summary>
    /// Gets the grace period Workable allowed before force interruption was applied.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; init; }
}

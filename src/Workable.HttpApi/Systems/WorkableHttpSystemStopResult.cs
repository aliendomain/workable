namespace Workable;

public sealed record WorkableHttpSystemStopResult(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State,
    IReadOnlyList<WorkerSnapshot> ForceInterruptedWorkers)
{
    public IReadOnlyList<WorkerSnapshot> CancellationRequestedWorkers { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> CancellationRequestedWorkerSummaries { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> ForceInterruptedWorkerSummaries { get; init; } = [];

    public IReadOnlyList<string> ForceInterruptedWorkerNames { get; init; } = [];

    public TimeSpan ShutdownGracePeriod { get; init; }
}

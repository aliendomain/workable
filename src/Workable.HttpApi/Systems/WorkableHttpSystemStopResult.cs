namespace Workable;

public sealed record WorkableHttpSystemStopResult(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State,
    IReadOnlyList<WorkerSnapshot> ForceCanceledWorkers)
{
    public IReadOnlyList<WorkerSnapshot> CancellationRequestedWorkers { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> CancellationRequestedWorkerSummaries { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> ForceCanceledWorkerSummaries { get; init; } = [];

    public IReadOnlyList<string> ForceCanceledWorkerNames { get; init; } = [];

    public TimeSpan ShutdownGracePeriod { get; init; }
}

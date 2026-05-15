namespace Workable;

public sealed record WorkSystemStopResult(
    IReadOnlyList<WorkerSnapshot> ForceCanceledWorkers)
{
    public IReadOnlyList<WorkerSnapshot> CancellationRequestedWorkers { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> CancellationRequestedWorkerSummaries { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> ForceCanceledWorkerSummaries { get; init; } = [];

    public IReadOnlyList<string> ForceCanceledWorkerNames
        => [.. this.ForceCanceledWorkerSummaries.Select(worker => worker.DefinitionName)];

    public TimeSpan ShutdownGracePeriod { get; init; }
}

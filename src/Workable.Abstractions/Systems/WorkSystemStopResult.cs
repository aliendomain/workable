namespace Workable;

public sealed record WorkSystemStopResult(
    IReadOnlyList<WorkerSnapshot> ForceInterruptedWorkers)
{
    public IReadOnlyList<WorkerSnapshot> CancellationRequestedWorkers { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> CancellationRequestedWorkerSummaries { get; init; } = [];

    public IReadOnlyList<WorkSystemShutdownWorker> ForceInterruptedWorkerSummaries { get; init; } = [];

    public IReadOnlyList<string> ForceInterruptedWorkerNames
        => [.. this.ForceInterruptedWorkerSummaries.Select(worker => worker.DefinitionName)];

    public TimeSpan ShutdownGracePeriod { get; init; }
}

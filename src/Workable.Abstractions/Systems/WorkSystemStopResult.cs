namespace Workable;

public sealed record WorkSystemStopResult(
    IReadOnlyList<WorkerSnapshot> ForceCanceledWorkers);

namespace Workable;

public sealed record WorkableHttpSystemStopResult(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State,
    IReadOnlyList<WorkerSnapshot> ForceCanceledWorkers);

namespace Workable;

public sealed record WorkableHttpSystemLifecycleResult(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State);

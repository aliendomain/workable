namespace Workable;

public sealed record WorkableHttpSystemDiagnostics(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State,
    WorkSystemQueueDiagnostics Queue,
    WorkSystemReadModelDiagnostics ReadModel,
    WorkSystemRetentionDiagnostics Retention);

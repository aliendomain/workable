namespace Workable;

public sealed record WorkableHttpSystemDiagnostics(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State,
    WorkSystemReadModelDiagnostics ReadModel,
    WorkSystemRetentionDiagnostics Retention);

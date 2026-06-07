namespace Workable;

public sealed record WorkableHttpSystemDiagnostics(
    string? Name,
    WorkSystemState State,
    WorkSystemQueueDiagnostics Queue,
    WorkSystemReadModelDiagnostics ReadModel,
    WorkSystemRetentionDiagnostics Retention,
    WorkSystemConcurrencyDiagnostics Concurrency,
    WorkSystemDurabilityDiagnostics Durability,
    WorkSystemIdempotencyDiagnostics Idempotency);

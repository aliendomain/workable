namespace Workable;

/// <summary>
/// Represents the HTTP diagnostics payload for one system.
/// </summary>
/// <param name="Name">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
/// <param name="State">The current lifecycle state of the system.</param>
/// <param name="Queue">Queue rejection and queue-health diagnostics.</param>
/// <param name="ReadModel">Read-model projection and freshness diagnostics.</param>
/// <param name="Retention">Final-worker retention and purge diagnostics.</param>
/// <param name="Concurrency">Deferred-start and capacity diagnostics.</param>
/// <param name="Durability">Durable coordination and cleanup diagnostics.</param>
/// <param name="Idempotency">Duplicate-rejection diagnostics.</param>
public sealed record WorkableHttpSystemDiagnostics(
    string? Name,
    WorkSystemState State,
    WorkSystemQueueDiagnostics Queue,
    WorkSystemReadModelDiagnostics ReadModel,
    WorkSystemRetentionDiagnostics Retention,
    WorkSystemConcurrencyDiagnostics Concurrency,
    WorkSystemDurabilityDiagnostics Durability,
    WorkSystemIdempotencyDiagnostics Idempotency);

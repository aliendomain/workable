namespace Workable;

/// <summary>
/// Exposes operator-oriented health diagnostics for a Workable system.
/// </summary>
public interface IWorkSystemDiagnostics
{
    /// <summary>
    /// Gets diagnostics about rejected queue requests.
    /// </summary>
    WorkSystemQueueDiagnostics Queue { get; }

    /// <summary>
    /// Gets diagnostics about the query read-model projector.
    /// </summary>
    WorkSystemReadModelDiagnostics ReadModel { get; }

    /// <summary>
    /// Gets diagnostics about final-worker retention and purge scheduling.
    /// </summary>
    WorkSystemRetentionDiagnostics Retention { get; }

    /// <summary>
    /// Gets diagnostics about deferred starts caused by concurrency limits.
    /// </summary>
    WorkSystemConcurrencyDiagnostics Concurrency { get; }

    /// <summary>
    /// Gets diagnostics about durable queue materialization, lease renewal, and cleanup.
    /// </summary>
    WorkSystemDurabilityDiagnostics Durability { get; }

    /// <summary>
    /// Gets diagnostics about duplicate-subject idempotency rejections.
    /// </summary>
    WorkSystemIdempotencyDiagnostics Idempotency { get; }
}

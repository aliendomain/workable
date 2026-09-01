namespace Workable;

/// <summary>
/// Describes persistent execution-diagnostics availability and initialization health.
/// </summary>
/// <param name="Status">The current initialization health status.</param>
/// <param name="InitializationFailedAt">The UTC time initialization failed, when <paramref name="Status"/> is <see cref="WorkExecutionDiagnosticsPersistenceHealthStatus.Unhealthy"/>.</param>
public sealed record WorkSystemExecutionDiagnosticsPersistenceDiagnostics(
    WorkExecutionDiagnosticsPersistenceHealthStatus Status,
    DateTimeOffset? InitializationFailedAt)
{
    /// <summary>
    /// Gets the shared snapshot used when persistence is not configured.
    /// </summary>
    public static WorkSystemExecutionDiagnosticsPersistenceDiagnostics NotConfigured { get; }
        = new(WorkExecutionDiagnosticsPersistenceHealthStatus.NotConfigured, null);

    /// <summary>
    /// Gets the shared snapshot used before repository initialization completes.
    /// </summary>
    public static WorkSystemExecutionDiagnosticsPersistenceDiagnostics PendingInitialization { get; }
        = new(WorkExecutionDiagnosticsPersistenceHealthStatus.PendingInitialization, null);

    /// <summary>
    /// Gets the shared snapshot used after successful repository initialization.
    /// </summary>
    public static WorkSystemExecutionDiagnosticsPersistenceDiagnostics Healthy { get; }
        = new(WorkExecutionDiagnosticsPersistenceHealthStatus.Healthy, null);

    /// <summary>
    /// Gets whether persistent execution diagnostics are currently available.
    /// </summary>
    public bool PersistenceAvailable => this.Status is
        WorkExecutionDiagnosticsPersistenceHealthStatus.PendingInitialization or
        WorkExecutionDiagnosticsPersistenceHealthStatus.Healthy;

    /// <summary>
    /// Gets whether execution-diagnostics persistence is healthy or is not configured.
    /// </summary>
    public bool IsHealthy => this.Status != WorkExecutionDiagnosticsPersistenceHealthStatus.Unhealthy;
}

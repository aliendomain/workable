namespace Workable;

/// <summary>
/// Describes the initialization health of persistent execution diagnostics.
/// </summary>
public enum WorkExecutionDiagnosticsPersistenceHealthStatus
{
    /// <summary>
    /// No execution-diagnostics persistence repository is registered.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// A repository is registered but has not completed initialization yet.
    /// </summary>
    PendingInitialization,

    /// <summary>
    /// The repository initialized successfully and persistence is available.
    /// </summary>
    Healthy,

    /// <summary>
    /// Repository initialization failed and persistence is unavailable for the rest of this process lifetime.
    /// </summary>
    Unhealthy,
}

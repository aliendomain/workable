namespace Workable;

/// <summary>
/// Represents system-level permissions that may be granted independently of per-definition access.
/// </summary>
public enum WorkSystemPermission
{
    /// <summary>
    /// Allows the caller to access the system shell and catalog.
    /// </summary>
    AccessSystem,

    /// <summary>
    /// Allows the caller to view diagnostics and operational state.
    /// </summary>
    ViewDiagnostics,

    /// <summary>
    /// Allows the caller to control system and worker operations.
    /// </summary>
    ControlSystem,
}

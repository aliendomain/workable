namespace Workable;

/// <summary>
/// Thrown when a caller lacks a required system-level permission.
/// </summary>
/// <param name="permission">The missing permission.</param>
/// <param name="systemId">The identifier of the affected system.</param>
/// <param name="systemName">The configured system name, when one exists.</param>
public sealed class WorkSystemAccessDeniedException(
    WorkSystemPermission permission,
    WorkSystemId systemId,
    string? systemName) : InvalidOperationException(CreateMessage(permission, systemName))
{
    /// <summary>
    /// Gets the system-level permission the caller lacked.
    /// </summary>
    public WorkSystemPermission Permission { get; } = permission;

    /// <summary>
    /// Gets the identifier of the affected system.
    /// </summary>
    public WorkSystemId SystemId { get; } = systemId;

    /// <summary>
    /// Gets the configured system name, when one exists.
    /// </summary>
    public string? SystemName { get; } = systemName;

    private static string CreateMessage(WorkSystemPermission permission, string? systemName)
    {
        var name = string.IsNullOrWhiteSpace(systemName) ? "<default>" : systemName;
        return permission switch
        {
            WorkSystemPermission.AccessSystem => $"Access to Workable system '{name}' requires some system-level access.",
            WorkSystemPermission.ViewDiagnostics => $"Viewing diagnostics for Workable system '{name}' requires diagnostics permission.",
            WorkSystemPermission.ControlSystem => $"Controlling Workable system '{name}' requires control-system permission.",
            _ => $"Access to Workable system '{name}' was denied.",
        };
    }
}

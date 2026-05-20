namespace Workable;

public sealed class WorkSystemAccessDeniedException(
    WorkSystemPermission permission,
    WorkSystemId systemId,
    string? systemName) : InvalidOperationException(CreateMessage(permission, systemName))
{
    public WorkSystemPermission Permission { get; } = permission;

    public WorkSystemId SystemId { get; } = systemId;

    public string? SystemName { get; } = systemName;

    private static string CreateMessage(WorkSystemPermission permission, string? systemName)
    {
        var name = string.IsNullOrWhiteSpace(systemName) ? "<default>" : systemName;
        return permission switch
        {
            WorkSystemPermission.Connect => $"Access to Workable system '{name}' requires connect permission.",
            WorkSystemPermission.ViewDiagnostics => $"Viewing diagnostics for Workable system '{name}' requires diagnostics permission.",
            WorkSystemPermission.ControlSystem => $"Controlling Workable system '{name}' requires control-system permission.",
            _ => $"Access to Workable system '{name}' was denied.",
        };
    }
}

namespace Workable;

/// <summary>
/// Configures host-level authorization groups for one Workable system.
/// </summary>
/// <remarks>
/// These groups apply system-wide permissions such as diagnostics, system control, and broad read or operate access
/// across all work definitions in the system.
/// </remarks>
public sealed record WorkSystemAuthorizationConfiguration
{
    /// <summary>
    /// Gets the default system authorization configuration with no groups assigned.
    /// </summary>
    public static WorkSystemAuthorizationConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the groups that receive system-administrator semantics.
    /// </summary>
    public IReadOnlySet<string> SystemAdministratorGroups { get; init; } = EmptySet();

    /// <summary>
    /// Gets the groups that receive work-administrator semantics.
    /// </summary>
    public IReadOnlySet<string> WorkAdministratorGroups { get; init; } = EmptySet();

    /// <summary>
    /// Gets the groups that can view system diagnostics.
    /// </summary>
    public IReadOnlySet<string> DiagnosticsGroups { get; init; } = EmptySet();

    /// <summary>
    /// Gets the groups that can start and stop the system.
    /// </summary>
    public IReadOnlySet<string> ControlSystemGroups { get; init; } = EmptySet();

    /// <summary>
    /// Gets the groups that can use Workable's built-in HTTP API surface for the system.
    /// </summary>
    public IReadOnlySet<string> BuiltInHttpApiSurfaceGroups { get; init; } = EmptySet();

    /// <summary>
    /// Gets the groups that can read every work definition in the system.
    /// </summary>
    public IReadOnlySet<string> ReadAllWorkGroups { get; init; } = EmptySet();

    /// <summary>
    /// Gets the groups that can operate every work definition in the system.
    /// </summary>
    public IReadOnlySet<string> OperateAllWorkGroups { get; init; } = EmptySet();

    private static IReadOnlySet<string> EmptySet()
        => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

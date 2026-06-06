namespace Workable;

public sealed record WorkSystemAuthorizationConfiguration
{
    public static WorkSystemAuthorizationConfiguration Default { get; } = new();

    public IReadOnlySet<string> SystemAdministratorGroups { get; init; } = EmptySet();

    public IReadOnlySet<string> WorkAdministratorGroups { get; init; } = EmptySet();

    public IReadOnlySet<string> DiagnosticsGroups { get; init; } = EmptySet();

    public IReadOnlySet<string> ControlSystemGroups { get; init; } = EmptySet();

    public IReadOnlySet<string> ReadAllWorkGroups { get; init; } = EmptySet();

    public IReadOnlySet<string> OperateAllWorkGroups { get; init; } = EmptySet();

    private static IReadOnlySet<string> EmptySet()
        => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

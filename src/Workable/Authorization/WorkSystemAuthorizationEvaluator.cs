namespace Workable;

internal sealed class WorkSystemAuthorizationEvaluator(
    WorkSystemAuthorizationConfiguration configuration,
    IReadOnlySet<string> groups)
{
    public bool CanConnect()
        => this.IsSystemAdministrator() || this.IsSatisfied(configuration.ConnectGroups);

    public bool CanViewDiagnostics()
        => this.IsSystemAdministrator() || this.IsSatisfied(configuration.DiagnosticsGroups);

    public bool CanControlSystem()
        => this.IsSystemAdministrator() || this.IsSatisfied(configuration.ControlSystemGroups);

    public bool HasReadAllWorkAccess()
        => this.IsSystemAdministrator()
            || this.IsWorkAdministrator()
            || this.IsSatisfied(configuration.ReadAllWorkGroups);

    public bool HasOperateAllWorkAccess()
        => this.IsWorkAdministrator()
            || this.IsSatisfied(configuration.OperateAllWorkGroups);

    private bool IsSystemAdministrator()
        => groups.Contains(InternalWorkAuthorizationGroups.SystemAdministrator)
            || this.IsSatisfied(configuration.SystemAdministratorGroups);

    private bool IsWorkAdministrator()
        => this.IsSatisfied(configuration.WorkAdministratorGroups);

    private bool IsSatisfied(IReadOnlySet<string> allowedGroups)
        => allowedGroups.Count > 0 && groups.Any(allowedGroups.Contains);
}

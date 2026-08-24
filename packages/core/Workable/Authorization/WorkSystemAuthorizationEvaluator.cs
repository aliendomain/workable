namespace Workable;

internal sealed class WorkSystemAuthorizationEvaluator(
    WorkSystemAuthorizationConfiguration configuration,
    IReadOnlySet<string> groups)
{
    public bool CanViewDiagnostics()
        => this.IsSystemAdministrator() || this.IsSatisfied(configuration.DiagnosticsGroups);

    public bool CanControlSystem()
        => this.IsSystemAdministrator() || this.IsSatisfied(configuration.ControlSystemGroups);

    public bool CanUseBuiltInHttpApiSurface()
        => this.IsSystemAdministrator()
            || this.IsWorkAdministrator()
            || this.IsSatisfied(configuration.BuiltInHttpApiSurfaceGroups);

    public bool HasReadAllWorkAccess()
        => this.IsSystemAdministrator()
            || this.IsWorkAdministrator()
            || this.IsSatisfied(configuration.ReadAllWorkGroups);

    public bool HasOperateAllWorkAccess()
        => this.IsWorkAdministrator()
            || this.IsSatisfied(configuration.OperateAllWorkGroups);

    public bool IsSystemAdministrator()
        => groups.Contains(InternalWorkAuthorizationGroups.SystemAdministrator)
            || this.IsSatisfied(configuration.SystemAdministratorGroups);

    public bool IsWorkAdministrator()
        => this.IsSatisfied(configuration.WorkAdministratorGroups);

    private bool IsSatisfied(IReadOnlySet<string> allowedGroups)
    {
        if (allowedGroups.Count == 0 || groups.Count == 0)
        {
            return false;
        }

        foreach (var group in groups)
        {
            if (allowedGroups.Contains(group))
            {
                return true;
            }
        }

        return false;
    }
}

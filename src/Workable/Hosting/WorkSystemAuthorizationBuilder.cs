namespace Workable;

internal sealed class WorkSystemAuthorizationBuilder(
    WorkSystemAuthorizationConfiguration configuration) : IWorkSystemAuthorizationBuilder
{
    private IReadOnlySet<string> systemAdministratorGroups = configuration.SystemAdministratorGroups;
    private IReadOnlySet<string> workAdministratorGroups = configuration.WorkAdministratorGroups;
    private IReadOnlySet<string> diagnosticsGroups = configuration.DiagnosticsGroups;
    private IReadOnlySet<string> controlSystemGroups = configuration.ControlSystemGroups;
    private IReadOnlySet<string> readAllWorkGroups = configuration.ReadAllWorkGroups;
    private IReadOnlySet<string> operateAllWorkGroups = configuration.OperateAllWorkGroups;

    public IWorkSystemAuthorizationBuilder SystemAdministrators(params string[] groups)
    {
        this.systemAdministratorGroups = ToSet(groups);
        return this;
    }

    public IWorkSystemAuthorizationBuilder WorkAdministrators(params string[] groups)
    {
        this.workAdministratorGroups = ToSet(groups);
        return this;
    }

    public IWorkSystemAuthorizationBuilder AllowDiagnosticsToGroups(params string[] groups)
    {
        this.diagnosticsGroups = ToSet(groups);
        return this;
    }

    public IWorkSystemAuthorizationBuilder AllowControlSystemToGroups(params string[] groups)
    {
        this.controlSystemGroups = ToSet(groups);
        return this;
    }

    public IWorkSystemAuthorizationBuilder AllowReadAllWorkToGroups(params string[] groups)
    {
        this.readAllWorkGroups = ToSet(groups);
        return this;
    }

    public IWorkSystemAuthorizationBuilder AllowOperateAllWorkToGroups(params string[] groups)
    {
        this.operateAllWorkGroups = ToSet(groups);
        return this;
    }

    internal WorkSystemAuthorizationConfiguration Build()
        => WorkSystemAuthorizationConfiguration.Default with
        {
            SystemAdministratorGroups = this.systemAdministratorGroups,
            WorkAdministratorGroups = this.workAdministratorGroups,
            DiagnosticsGroups = this.diagnosticsGroups,
            ControlSystemGroups = this.controlSystemGroups,
            ReadAllWorkGroups = this.readAllWorkGroups,
            OperateAllWorkGroups = this.operateAllWorkGroups,
        };

    private static IReadOnlySet<string> ToSet(IEnumerable<string>? groups)
        => groups is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                groups
                    .Where(group => !string.IsNullOrWhiteSpace(group))
                    .Select(group => group.Trim()),
                StringComparer.OrdinalIgnoreCase);
}

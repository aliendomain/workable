namespace Workable;

internal sealed class WorkAuthorizationBuilder : IWorkAuthorizationBuilder
{
    private IEnumerable<string>? readGroups;
    private IEnumerable<string>? operateGroups;
    private bool operateKnownAuthenticatedUsers;

    public IWorkAuthorizationBuilder RequireGroups(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null)
    {
        this.readGroups = readGroups;
        this.operateGroups = operateGroups;
        this.operateKnownAuthenticatedUsers = false;
        return this;
    }

    public IWorkAuthorizationBuilder AllowReadToGroups(params string[] groups)
    {
        this.readGroups = groups;
        return this;
    }

    public IWorkAuthorizationBuilder AllowOperateToGroups(params string[] groups)
    {
        this.operateGroups = groups;
        return this;
    }

    public IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers()
    {
        this.operateKnownAuthenticatedUsers = true;
        return this;
    }

    internal WorkDefinitionAuthorization Build()
        => WorkDefinitionAuthorization.Create(
            this.readGroups,
            this.operateGroups,
            WorkAuthorizationRegistrationSource.Fluent,
            operateKnownAuthenticatedUsers: this.operateKnownAuthenticatedUsers);
}

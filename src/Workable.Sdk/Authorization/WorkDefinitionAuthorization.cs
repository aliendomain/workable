namespace Workable;

public sealed record WorkDefinitionAuthorization(
    WorkAuthorizationRequirement Read,
    WorkAuthorizationRequirement Operate)
{
    public static WorkDefinitionAuthorization None
        => Create(source: WorkAuthorizationRegistrationSource.None);

    public static WorkDefinitionAuthorization Create(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null,
        WorkAuthorizationRegistrationSource source = WorkAuthorizationRegistrationSource.None,
        bool readKnownAuthenticatedUsers = false,
        bool operateKnownAuthenticatedUsers = false)
        => new(
            WorkAuthorizationRequirement.Create(readGroups, source, readKnownAuthenticatedUsers),
            WorkAuthorizationRequirement.Create(operateGroups, source, operateKnownAuthenticatedUsers));

    public bool CanRead(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser = false)
        => this.Read.IsSatisfiedBy(groups, isKnownAuthenticatedUser);

    public bool CanOperate(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser = false)
        => this.Operate.IsSatisfiedBy(groups, isKnownAuthenticatedUser);
}

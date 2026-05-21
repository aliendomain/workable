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
        WorkAuthorizationRegistrationSource source = WorkAuthorizationRegistrationSource.None)
        => new(
            WorkAuthorizationRequirement.Create(readGroups, source),
            WorkAuthorizationRequirement.Create(operateGroups, source));

    public bool CanRead(IReadOnlySet<string> groups)
        => this.Read.IsSatisfiedBy(groups);

    public bool CanOperate(IReadOnlySet<string> groups)
        => this.Operate.IsSatisfiedBy(groups);
}

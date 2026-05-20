namespace Workable;

public sealed record WorkAuthorizationRequirement(
    IReadOnlySet<string> Groups,
    WorkAuthorizationRegistrationSource Source)
{
    public static WorkAuthorizationRequirement None
        => Create(source: WorkAuthorizationRegistrationSource.None);

    public static WorkAuthorizationRequirement Create(
        IEnumerable<string>? groups = null,
        WorkAuthorizationRegistrationSource source = WorkAuthorizationRegistrationSource.None)
    {
        var groupSet = ToSet(groups);
        return new(
            groupSet,
            groupSet.Count > 0
                ? source
                : WorkAuthorizationRegistrationSource.None);
    }

    public bool IsSatisfiedBy(IReadOnlySet<string> groups)
        => this.Groups.Count > 0 && groups.Any(this.Groups.Contains);

    private static HashSet<string> ToSet(IEnumerable<string>? groups)
        => groups is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(
                groups
                    .Where(group => !string.IsNullOrWhiteSpace(group))
                    .Select(group => group.Trim()),
                StringComparer.OrdinalIgnoreCase);
}

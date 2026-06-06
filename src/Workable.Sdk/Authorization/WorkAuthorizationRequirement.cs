namespace Workable;

public sealed record WorkAuthorizationRequirement(
    IReadOnlySet<string> Groups,
    WorkAuthorizationRegistrationSource Source,
    bool AllowsKnownAuthenticatedUsers = false)
{
    public static WorkAuthorizationRequirement None
        => Create(source: WorkAuthorizationRegistrationSource.None);

    public static WorkAuthorizationRequirement Create(
        IEnumerable<string>? groups = null,
        WorkAuthorizationRegistrationSource source = WorkAuthorizationRegistrationSource.None,
        bool allowsKnownAuthenticatedUsers = false)
    {
        var groupSet = ToSet(groups);
        return new(
            groupSet,
            groupSet.Count > 0 || allowsKnownAuthenticatedUsers
                ? source
                : WorkAuthorizationRegistrationSource.None,
            allowsKnownAuthenticatedUsers);
    }

    public bool IsSatisfiedBy(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser = false)
        => (this.Groups.Count > 0 && groups.Any(this.Groups.Contains)) ||
            (this.AllowsKnownAuthenticatedUsers && isKnownAuthenticatedUser);

    private static HashSet<string> ToSet(IEnumerable<string>? groups)
        => groups is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(
                groups
                    .Where(group => !string.IsNullOrWhiteSpace(group))
                    .Select(group => group.Trim()),
                StringComparer.OrdinalIgnoreCase);
}

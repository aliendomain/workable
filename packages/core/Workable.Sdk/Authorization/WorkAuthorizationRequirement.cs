namespace Workable;

/// <summary>
/// Represents one read or operate authorization requirement for a work definition.
/// </summary>
/// <param name="Groups">The groups that satisfy the requirement.</param>
/// <param name="Source">The source from which the requirement was configured.</param>
/// <param name="AllowsKnownAuthenticatedUsers">Whether a known authenticated actor also satisfies the requirement without group membership.</param>
public sealed record WorkAuthorizationRequirement(
    IReadOnlySet<string> Groups,
    WorkAuthorizationRegistrationSource Source,
    bool AllowsKnownAuthenticatedUsers = false)
{
    /// <summary>
    /// Gets a requirement that allows no callers.
    /// </summary>
    public static WorkAuthorizationRequirement None
        => Create(source: WorkAuthorizationRegistrationSource.None);

    /// <summary>
    /// Creates an authorization requirement from raw group values.
    /// </summary>
    /// <param name="groups">The raw group values to normalize.</param>
    /// <param name="source">The source from which the requirement was configured.</param>
    /// <param name="allowsKnownAuthenticatedUsers">Whether a known authenticated actor also satisfies the requirement.</param>
    /// <returns>The created authorization requirement.</returns>
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

    /// <summary>
    /// Determines whether the supplied caller groups satisfy the requirement.
    /// </summary>
    /// <param name="groups">The caller groups to test.</param>
    /// <param name="isKnownAuthenticatedUser">Whether the caller is represented by a known authenticated actor.</param>
    /// <returns><see langword="true"/> when the requirement is satisfied; otherwise <see langword="false"/>.</returns>
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

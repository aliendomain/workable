namespace Workable;

/// <summary>
/// Represents the discover, read, and operate authorization attached to a work definition.
/// </summary>
/// <param name="Read">The requirement that controls read surfaces.</param>
/// <param name="Operate">The requirement that controls queueing, worker-action, and reconfiguration surfaces.</param>
public sealed record WorkDefinitionAuthorization(
    WorkAuthorizationRequirement Read,
    WorkAuthorizationRequirement Operate)
{
    /// <summary>
    /// Gets the requirement that controls redacted definition and schema discovery.
    /// </summary>
    public WorkAuthorizationRequirement Discover { get; init; } = WorkAuthorizationRequirement.None;

    /// <summary>
    /// Gets an authorization value that allows no callers.
    /// </summary>
    public static WorkDefinitionAuthorization None
        => Create(source: WorkAuthorizationRegistrationSource.None);

    /// <summary>
    /// Creates definition authorization from raw discover, read, and operate groups.
    /// </summary>
    /// <param name="readGroups">The groups allowed to read the definition.</param>
    /// <param name="operateGroups">The groups allowed to queue, operate, or reconfigure the definition.</param>
    /// <param name="source">The source from which the authorization was configured.</param>
    /// <param name="readKnownAuthenticatedUsers">Whether a known authenticated actor satisfies the read requirement.</param>
    /// <param name="operateKnownAuthenticatedUsers">Whether a known authenticated actor satisfies the operate requirement.</param>
    /// <param name="discoverGroups">The groups allowed to discover the definition and its schemas.</param>
    /// <param name="discoverKnownAuthenticatedUsers">Whether a known authenticated actor satisfies the discover requirement.</param>
    /// <returns>The created definition authorization.</returns>
    public static WorkDefinitionAuthorization Create(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null,
        WorkAuthorizationRegistrationSource source = WorkAuthorizationRegistrationSource.None,
        bool readKnownAuthenticatedUsers = false,
        bool operateKnownAuthenticatedUsers = false,
        IEnumerable<string>? discoverGroups = null,
        bool discoverKnownAuthenticatedUsers = false)
        => new(
            WorkAuthorizationRequirement.Create(readGroups, source, readKnownAuthenticatedUsers),
            WorkAuthorizationRequirement.Create(operateGroups, source, operateKnownAuthenticatedUsers))
        {
            Discover = WorkAuthorizationRequirement.Create(
                discoverGroups,
                source,
                discoverKnownAuthenticatedUsers),
        };

    /// <summary>
    /// Determines whether the supplied caller groups may discover the definition and its schemas.
    /// Read or operate access also implies discovery access.
    /// </summary>
    /// <param name="groups">The caller groups to test.</param>
    /// <param name="isKnownAuthenticatedUser">Whether the caller is represented by a known authenticated actor.</param>
    /// <returns><see langword="true"/> when discovery access is allowed; otherwise <see langword="false"/>.</returns>
    public bool CanDiscover(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser = false)
        => this.Discover.IsSatisfiedBy(groups, isKnownAuthenticatedUser) ||
            this.CanRead(groups, isKnownAuthenticatedUser) ||
            this.CanOperate(groups, isKnownAuthenticatedUser);

    /// <summary>
    /// Determines whether the supplied caller groups satisfy the read requirement.
    /// </summary>
    /// <param name="groups">The caller groups to test.</param>
    /// <param name="isKnownAuthenticatedUser">Whether the caller is represented by a known authenticated actor.</param>
    /// <returns><see langword="true"/> when read access is allowed; otherwise <see langword="false"/>.</returns>
    public bool CanRead(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser = false)
        => this.Read.IsSatisfiedBy(groups, isKnownAuthenticatedUser);

    /// <summary>
    /// Determines whether the supplied caller groups satisfy the operate requirement.
    /// </summary>
    /// <param name="groups">The caller groups to test.</param>
    /// <param name="isKnownAuthenticatedUser">Whether the caller is represented by a known authenticated actor.</param>
    /// <returns><see langword="true"/> when operate access is allowed; otherwise <see langword="false"/>.</returns>
    public bool CanOperate(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser = false)
        => this.Operate.IsSatisfiedBy(groups, isKnownAuthenticatedUser);
}

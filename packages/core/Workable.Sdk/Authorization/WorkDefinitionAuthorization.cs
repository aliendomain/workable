namespace Workable;

/// <summary>
/// Represents the read and operate authorization attached to a work definition.
/// </summary>
/// <param name="Read">The requirement that controls read surfaces.</param>
/// <param name="Operate">The requirement that controls queueing, worker-action, and reconfiguration surfaces.</param>
public sealed record WorkDefinitionAuthorization(
    WorkAuthorizationRequirement Read,
    WorkAuthorizationRequirement Operate)
{
    /// <summary>
    /// Gets an authorization value that allows no callers.
    /// </summary>
    public static WorkDefinitionAuthorization None
        => Create(source: WorkAuthorizationRegistrationSource.None);

    /// <summary>
    /// Creates definition authorization from raw read and operate groups.
    /// </summary>
    /// <param name="readGroups">The groups allowed to read the definition.</param>
    /// <param name="operateGroups">The groups allowed to queue, operate, or reconfigure the definition.</param>
    /// <param name="source">The source from which the authorization was configured.</param>
    /// <param name="readKnownAuthenticatedUsers">Whether a known authenticated actor satisfies the read requirement.</param>
    /// <param name="operateKnownAuthenticatedUsers">Whether a known authenticated actor satisfies the operate requirement.</param>
    /// <returns>The created definition authorization.</returns>
    public static WorkDefinitionAuthorization Create(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null,
        WorkAuthorizationRegistrationSource source = WorkAuthorizationRegistrationSource.None,
        bool readKnownAuthenticatedUsers = false,
        bool operateKnownAuthenticatedUsers = false)
        => new(
            WorkAuthorizationRequirement.Create(readGroups, source, readKnownAuthenticatedUsers),
            WorkAuthorizationRequirement.Create(operateGroups, source, operateKnownAuthenticatedUsers));

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

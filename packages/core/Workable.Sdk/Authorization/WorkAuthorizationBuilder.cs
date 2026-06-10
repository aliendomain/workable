namespace Workable;

internal sealed class WorkAuthorizationBuilder : IWorkAuthorizationBuilder
{
    private IEnumerable<string>? readGroups;
    private readonly List<WorkOperateAuthorizationGrant> groupOperateGrants = [];
    private WorkOperateAuthorizationGrant? knownAuthenticatedOperateGrant;
    private int knownAuthenticatedOperateGrantCount;

    /// <summary>
    /// Replaces both read and operate group requirements.
    /// </summary>
    /// <param name="readGroups">The groups allowed to read the definition.</param>
    /// <param name="operateGroups">The groups allowed to queue and operate the definition.</param>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder RequireGroups(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null)
    {
        this.readGroups = readGroups;
        this.groupOperateGrants.Clear();
        this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(ToSet(operateGroups), false, []));
        this.knownAuthenticatedOperateGrant = null;
        this.knownAuthenticatedOperateGrantCount = 0;
        return this;
    }

    /// <summary>
    /// Replaces the read groups for the definition.
    /// </summary>
    /// <param name="groups">The groups allowed to read the definition.</param>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowReadToGroups(params string[] groups)
    {
        this.readGroups = groups;
        return this;
    }

    /// <summary>
    /// Replaces the operate groups for the definition.
    /// </summary>
    /// <param name="groups">The groups allowed to queue and operate the definition.</param>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowOperateToGroups(params string[] groups)
    {
        this.groupOperateGrants.Clear();
        this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(ToSet(groups), false, []));
        return this;
    }

    public IWorkAuthorizationBuilder AllowOperateToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var requirements = new WorkOperateRequirementBuilder();
        configure(requirements);
        this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(
            ToSet(groups),
            false,
            requirements.Build()));
        return this;
    }

    /// <summary>
    /// Allows operate access to callers represented by a known authenticated actor.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers()
    {
        this.knownAuthenticatedOperateGrant = new WorkOperateAuthorizationGrant(ToSet([]), true, []);
        this.knownAuthenticatedOperateGrantCount++;
        return this;
    }

    public IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var requirements = new WorkOperateRequirementBuilder();
        configure(requirements);
        this.knownAuthenticatedOperateGrant = new WorkOperateAuthorizationGrant(ToSet([]), true, requirements.Build());
        this.knownAuthenticatedOperateGrantCount++;
        return this;
    }

    internal WorkDefinitionAuthorization Build()
        => this.BuildRegistration().DefinitionAuthorization;

    internal WorkAuthorizationRegistration BuildRegistration()
    {
        var operateAuthorization = this.BuildOperateAuthorization();
        return new WorkAuthorizationRegistration(
            WorkDefinitionAuthorization.Create(
                this.readGroups,
                operateAuthorization.Groups,
                WorkAuthorizationRegistrationSource.Fluent,
                operateKnownAuthenticatedUsers: operateAuthorization.AllowsKnownAuthenticatedUsers),
            operateAuthorization);
    }

    private WorkOperateAuthorizationConfiguration BuildOperateAuthorization()
    {
        var grants = new List<WorkOperateAuthorizationGrant>(this.groupOperateGrants.Count + 1);
        grants.AddRange(this.groupOperateGrants);
        if (this.knownAuthenticatedOperateGrant is not null)
        {
            grants.Add(this.knownAuthenticatedOperateGrant);
        }

        if (this.knownAuthenticatedOperateGrantCount > 1)
        {
            grants.Add(new WorkOperateAuthorizationGrant(ToSet([]), true, []));
        }

        WorkOperateAuthorizationConfigurationValidator.ValidateOrThrow(grants);
        if (this.knownAuthenticatedOperateGrantCount > 1)
        {
            grants.RemoveAt(grants.Count - 1);
        }

        return grants.Count == 0
            ? WorkOperateAuthorizationConfiguration.None
            : new WorkOperateAuthorizationConfiguration(grants);
    }

    private static IReadOnlySet<string> ToSet(IEnumerable<string>? groups)
        => groups is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                groups
                    .Where(group => !string.IsNullOrWhiteSpace(group))
                    .Select(group => group.Trim()),
                StringComparer.OrdinalIgnoreCase);
}

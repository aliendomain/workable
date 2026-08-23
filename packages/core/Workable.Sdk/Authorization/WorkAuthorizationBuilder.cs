namespace Workable;

internal sealed class WorkAuthorizationBuilder : IWorkAuthorizationBuilder
{
    private IEnumerable<string>? discoverGroups;
    private bool discoverKnownAuthenticatedUsers;
    private IEnumerable<string>? readGroups;
    private bool readKnownAuthenticatedUsers;
    private readonly List<WorkOperateAuthorizationGrant> groupOperateGrants = [];
    private readonly List<WorkOperateAuthorizationGrant> knownAuthenticatedOperateGrants = [];

    /// <summary>
    /// Resets explicit discovery and replaces read and operate authorization with group-based requirements.
    /// </summary>
    /// <param name="readGroups">The groups allowed to read the definition.</param>
    /// <param name="operateGroups">The groups allowed to queue and operate the definition.</param>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder RequireGroups(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null)
    {
        this.discoverGroups = null;
        this.discoverKnownAuthenticatedUsers = false;
        this.readGroups = readGroups;
        this.readKnownAuthenticatedUsers = false;
        this.groupOperateGrants.Clear();
        this.knownAuthenticatedOperateGrants.Clear();

        var normalizedGroups = ToSet(operateGroups);
        if (normalizedGroups.Count > 0)
        {
            this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(
                normalizedGroups,
                false,
                WorkOperationPermissions.Operate,
                []));
        }

        return this;
    }

    /// <summary>
    /// Replaces the explicit discover groups for the definition.
    /// </summary>
    /// <param name="groups">The groups allowed to discover the definition and its schemas.</param>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowDiscoverToGroups(params string[] groups)
    {
        this.discoverGroups = groups;
        return this;
    }

    /// <summary>
    /// Allows discovery to callers represented by a known authenticated actor.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowDiscoverToKnownAuthenticatedUsers()
    {
        this.discoverKnownAuthenticatedUsers = true;
        return this;
    }

    /// <summary>
    /// Allows read access to callers represented by a known authenticated actor.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowReadToKnownAuthenticatedUsers()
    {
        this.readKnownAuthenticatedUsers = true;
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
    /// Replaces the broad group-based operate grant for the definition.
    /// </summary>
    /// <param name="groups">The groups allowed to queue, operate, and reconfigure the definition.</param>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowOperateToGroups(params string[] groups)
    {
        this.groupOperateGrants.Clear();

        var normalizedGroups = ToSet(groups);
        if (normalizedGroups.Count > 0)
        {
            this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(
                normalizedGroups,
                false,
                WorkOperationPermissions.Operate,
                []));
        }

        return this;
    }

    public IWorkAuthorizationBuilder AllowOperateToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure)
        => this.AllowOperationsToGroups(groups, WorkOperationPermissions.Operate, configure);

    public IWorkAuthorizationBuilder AllowQueueToGroups(params string[] groups)
        => this.AllowOperationsToGroups(groups, WorkOperationPermissions.Queue);

    public IWorkAuthorizationBuilder AllowQueueToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure)
        => this.AllowOperationsToGroups(groups, WorkOperationPermissions.Queue, configure);

    public IWorkAuthorizationBuilder AllowWorkerActionsToGroups(params string[] groups)
        => this.AllowOperationsToGroups(groups, WorkOperationPermissions.WorkerActions);

    public IWorkAuthorizationBuilder AllowWorkerActionsToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure)
        => this.AllowOperationsToGroups(groups, WorkOperationPermissions.WorkerActions, configure);

    public IWorkAuthorizationBuilder AllowOperationsToGroups(
        IEnumerable<string> groups,
        WorkOperationPermissions permissions)
    {
        var normalizedGroups = ToSet(groups);
        if (normalizedGroups.Count == 0)
        {
            return this;
        }

        this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(
            normalizedGroups,
            false,
            NormalizePermissions(permissions),
            []));
        return this;
    }

    public IWorkAuthorizationBuilder AllowOperationsToGroups(
        IEnumerable<string> groups,
        WorkOperationPermissions permissions,
        Action<IWorkOperateRequirementBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var normalizedGroups = ToSet(groups);
        if (normalizedGroups.Count == 0)
        {
            return this;
        }

        this.groupOperateGrants.Add(new WorkOperateAuthorizationGrant(
            normalizedGroups,
            false,
            NormalizePermissions(permissions),
            BuildRequirements(configure)));
        return this;
    }

    /// <summary>
    /// Allows operate access to callers represented by a known authenticated actor.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
    public IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers()
        => this.AllowOperationsToKnownAuthenticatedUsers(WorkOperationPermissions.Operate);

    public IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure)
        => this.AllowOperationsToKnownAuthenticatedUsers(WorkOperationPermissions.Operate, configure);

    public IWorkAuthorizationBuilder AllowQueueToKnownAuthenticatedUsers()
        => this.AllowOperationsToKnownAuthenticatedUsers(WorkOperationPermissions.Queue);

    public IWorkAuthorizationBuilder AllowQueueToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure)
        => this.AllowOperationsToKnownAuthenticatedUsers(WorkOperationPermissions.Queue, configure);

    public IWorkAuthorizationBuilder AllowWorkerActionsToKnownAuthenticatedUsers()
        => this.AllowOperationsToKnownAuthenticatedUsers(WorkOperationPermissions.WorkerActions);

    public IWorkAuthorizationBuilder AllowWorkerActionsToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure)
        => this.AllowOperationsToKnownAuthenticatedUsers(WorkOperationPermissions.WorkerActions, configure);

    public IWorkAuthorizationBuilder AllowOperationsToKnownAuthenticatedUsers(
        WorkOperationPermissions permissions)
    {
        this.knownAuthenticatedOperateGrants.Add(new WorkOperateAuthorizationGrant(
            ToSet([]),
            true,
            NormalizePermissions(permissions),
            []));
        return this;
    }

    public IWorkAuthorizationBuilder AllowOperationsToKnownAuthenticatedUsers(
        WorkOperationPermissions permissions,
        Action<IWorkOperateRequirementBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        this.knownAuthenticatedOperateGrants.Add(new WorkOperateAuthorizationGrant(
            ToSet([]),
            true,
            NormalizePermissions(permissions),
            BuildRequirements(configure)));
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
                readKnownAuthenticatedUsers: this.readKnownAuthenticatedUsers,
                operateKnownAuthenticatedUsers: operateAuthorization.AllowsKnownAuthenticatedUsers,
                discoverGroups: this.discoverGroups,
                discoverKnownAuthenticatedUsers: this.discoverKnownAuthenticatedUsers),
            operateAuthorization);
    }

    private WorkOperateAuthorizationConfiguration BuildOperateAuthorization()
    {
        var grants = new List<WorkOperateAuthorizationGrant>(
            this.groupOperateGrants.Count + this.knownAuthenticatedOperateGrants.Count);
        grants.AddRange(this.groupOperateGrants);
        grants.AddRange(this.knownAuthenticatedOperateGrants);

        WorkOperateAuthorizationConfigurationValidator.ValidateOrThrow(grants);

        return grants.Count == 0
            ? WorkOperateAuthorizationConfiguration.None
            : new WorkOperateAuthorizationConfiguration(grants);
    }

    private static IReadOnlyList<WorkOperateRequirementRegistration> BuildRequirements(
        Action<IWorkOperateRequirementBuilder> configure)
    {
        var requirements = new WorkOperateRequirementBuilder();
        configure(requirements);
        return requirements.Build();
    }

    private static WorkOperationPermissions NormalizePermissions(WorkOperationPermissions permissions)
    {
        if (permissions == WorkOperationPermissions.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permissions),
                permissions,
                "At least one work operation permission must be supplied.");
        }

        if ((permissions & ~WorkOperationPermissions.Operate) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permissions),
                permissions,
                "Only supported work operation permission flags may be supplied.");
        }

        return permissions;
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

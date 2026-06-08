namespace Workable;

internal sealed class WorkAuthorizationBuilder : IWorkAuthorizationBuilder
{
    private IEnumerable<string>? readGroups;
    private IEnumerable<string>? operateGroups;
    private bool operateKnownAuthenticatedUsers;

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
        this.operateGroups = operateGroups;
        this.operateKnownAuthenticatedUsers = false;
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
        this.operateGroups = groups;
        return this;
    }

    /// <summary>
    /// Allows operate access to callers represented by a known authenticated actor.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
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

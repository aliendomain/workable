namespace Workable;

/// <summary>
/// Provides the default configuration keys, claim names, and helper values used by <c>Workable.Entra</c>.
/// </summary>
public static class WorkableEntraAuthorizationDefaults
{
    /// <summary>
    /// The configuration section name used by <see cref="WorkableEntraServiceCollectionExtensions.AddWorkableEntraAuthorization(Microsoft.Extensions.DependencyInjection.IServiceCollection,Microsoft.Extensions.Configuration.IConfiguration)"/>.
    /// </summary>
    public const string ConfigurationSectionName = "Workable:Entra";

    /// <summary>
    /// The delegated-scope claim type.
    /// </summary>
    public const string ScopeClaimType = "scp";

    /// <summary>
    /// The Entra security-group claim type.
    /// </summary>
    public const string GroupsClaimType = "groups";

    /// <summary>
    /// The plural app-role claim type.
    /// </summary>
    public const string RolesClaimType = "roles";

    /// <summary>
    /// The singular app-role claim type.
    /// </summary>
    public const string RoleClaimType = "role";

    /// <summary>
    /// A suggested read-oriented delegated scope name.
    /// </summary>
    public const string ReadScope = "Workable.Read";

    /// <summary>
    /// A suggested execute-oriented delegated scope name.
    /// </summary>
    public const string ExecuteScope = "Workable.Execute";

    /// <summary>
    /// A suggested diagnostics-oriented delegated scope name.
    /// </summary>
    public const string DiagnosticsScope = "Workable.Diagnostics";

    /// <summary>
    /// A suggested control-oriented delegated scope name.
    /// </summary>
    public const string ControlScope = "Workable.Control";

    /// <summary>
    /// A suggested administrator delegated scope name.
    /// </summary>
    public const string AdminScope = "Workable.Admin";
}

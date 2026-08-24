using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace Workable;

/// <summary>
/// Configures how Workable uses identities produced by the host's Microsoft Entra authentication.
/// </summary>
public sealed class WorkableEntraAuthorizationOptions
{
    /// <summary>
    /// Gets or sets the existing ASP.NET Core authentication scheme Workable should use explicitly.
    /// When unset, Workable uses the principal produced by the host authentication pipeline.
    /// </summary>
    public string? AuthenticationScheme { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether delegated scope values should become Workable authorization groups.
    /// When disabled, matching claims are excluded unless an earlier host claim mapper handles them.
    /// </summary>
    public bool MapScopesToWorkableGroups { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Entra app-role values should become Workable authorization groups.
    /// When disabled, matching claims are excluded unless an earlier host claim mapper handles them.
    /// </summary>
    public bool MapAppRolesToWorkableGroups { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Entra security-group ids should become Workable authorization groups.
    /// When disabled, matching claims are excluded unless an earlier host claim mapper handles them.
    /// </summary>
    public bool MapGroupsToWorkableGroups { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional host predicate that identifies identities Workable.Entra should interpret.
    /// Supplying a predicate replaces the default classifier. When unset, an explicitly selected authentication
    /// scheme owns its resulting identity; ambient identities must contain a raw or standard mapped Entra object-id
    /// claim.
    /// </summary>
    public Func<ClaimsIdentity, bool>? IdentityPredicate { get; set; }

    internal static WorkableEntraAuthorizationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new WorkableEntraAuthorizationOptions
        {
            AuthenticationScheme = configuration["AuthenticationScheme"],
            MapScopesToWorkableGroups = ParseBoolean(
                configuration,
                "MapScopesToWorkableGroups",
                defaultValue: true),
            MapAppRolesToWorkableGroups = ParseBoolean(
                configuration,
                "MapAppRolesToWorkableGroups",
                defaultValue: true),
            MapGroupsToWorkableGroups = ParseBoolean(
                configuration,
                "MapGroupsToWorkableGroups",
                defaultValue: true),
        };

        return options;
    }

    internal static bool HasConfiguredValues(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration["AuthenticationScheme"] is not null ||
            configuration["MapScopesToWorkableGroups"] is not null ||
            configuration["MapAppRolesToWorkableGroups"] is not null ||
            configuration["MapGroupsToWorkableGroups"] is not null;
    }

    internal void ThrowIfInvalid()
    {
        if (this.AuthenticationScheme is not null &&
            string.IsNullOrWhiteSpace(this.AuthenticationScheme))
        {
            throw new InvalidOperationException(
                "Workable Entra authorization requires a non-empty authentication scheme.");
        }
    }

    private static bool ParseBoolean(
        IConfiguration configuration,
        string key,
        bool defaultValue)
    {
        var value = configuration[key];
        if (value is null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value.Trim(), out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Workable Entra configuration value '{key}' must be 'true' or 'false'.");
    }
}

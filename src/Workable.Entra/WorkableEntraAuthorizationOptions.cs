using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace Workable;

/// <summary>
/// Configures Microsoft Entra bearer authentication and group mapping for Workable ASP.NET Core surfaces.
/// </summary>
public sealed class WorkableEntraAuthorizationOptions
{
    /// <summary>
    /// Gets or sets the Microsoft Entra tenant identifier used to build the authority URL.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the primary accepted audience for Workable bearer tokens.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Gets the additional accepted audiences for the same host.
    /// </summary>
    public IList<string> AdditionalAudiences { get; } = [];

    /// <summary>
    /// Gets or sets the authority host used to validate tokens.
    /// </summary>
    public string AuthorityHost { get; set; } = WorkableEntraAuthorizationDefaults.AuthorityHost;

    /// <summary>
    /// Gets or sets the ASP.NET Core authentication scheme name Workable should register and use.
    /// </summary>
    public string AuthenticationScheme { get; set; } = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>
    /// Gets or sets a value indicating whether delegated scope values should become Workable authorization groups.
    /// </summary>
    public bool MapScopesToWorkableGroups { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Entra app-role values should become Workable authorization groups.
    /// </summary>
    public bool MapAppRolesToWorkableGroups { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Entra security-group ids should become Workable authorization groups.
    /// </summary>
    public bool MapGroupsToWorkableGroups { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether SignalR browser access tokens may be accepted from the query string on configured paths.
    /// </summary>
    public bool AllowSignalRAccessTokensFromQueryString { get; set; } = true;

    /// <summary>
    /// Gets or sets the query-string parameter name used for SignalR browser access tokens.
    /// </summary>
    public string SignalRAccessTokenQueryStringName { get; set; } =
        WorkableEntraAuthorizationDefaults.SignalRAccessTokenQueryStringName;

    /// <summary>
    /// Gets the absolute application paths on which SignalR query-string access tokens are accepted.
    /// </summary>
    public IList<string> SignalRAccessTokenQueryStringPaths { get; } =
        [WorkableEntraAuthorizationDefaults.SignalRHubPath];

    /// <summary>
    /// Gets the full v2 authority URL derived from <see cref="AuthorityHost"/> and <see cref="TenantId"/>.
    /// </summary>
    public string Authority
    {
        get
        {
            var authorityHost = this.AuthorityHost.TrimEnd('/');
            return $"{authorityHost}/{this.TenantId}/v2.0";
        }
    }

    internal static WorkableEntraAuthorizationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new WorkableEntraAuthorizationOptions
        {
            TenantId = configuration["TenantId"],
            Audience = configuration["Audience"],
            AuthorityHost = configuration["AuthorityHost"] ?? WorkableEntraAuthorizationDefaults.AuthorityHost,
            AuthenticationScheme = configuration["AuthenticationScheme"] ?? JwtBearerDefaults.AuthenticationScheme,
            MapScopesToWorkableGroups = ParseBoolean(
                configuration["MapScopesToWorkableGroups"],
                defaultValue: true),
            MapAppRolesToWorkableGroups = ParseBoolean(
                configuration["MapAppRolesToWorkableGroups"],
                defaultValue: true),
            MapGroupsToWorkableGroups = ParseBoolean(
                configuration["MapGroupsToWorkableGroups"],
                defaultValue: true),
            AllowSignalRAccessTokensFromQueryString = ParseBoolean(
                configuration["AllowSignalRAccessTokensFromQueryString"],
                defaultValue: true),
            SignalRAccessTokenQueryStringName =
                configuration["SignalRAccessTokenQueryStringName"] ??
                WorkableEntraAuthorizationDefaults.SignalRAccessTokenQueryStringName,
        };

        foreach (var audience in ReadList(configuration.GetSection("AdditionalAudiences")))
        {
            options.AdditionalAudiences.Add(audience);
        }

        var signalRPaths = configuration.GetSection("SignalRAccessTokenQueryStringPaths");
        if (signalRPaths.Exists())
        {
            options.SignalRAccessTokenQueryStringPaths.Clear();
            foreach (var path in ReadList(signalRPaths))
            {
                options.SignalRAccessTokenQueryStringPaths.Add(path);
            }
        }

        return options;
    }

    internal IReadOnlyList<string> GetAudiences()
    {
        var audiences = new HashSet<string>(StringComparer.Ordinal);
        AddAcceptedAudience(audiences, this.Audience);
        foreach (var audience in this.AdditionalAudiences)
        {
            AddAcceptedAudience(audiences, audience);
        }

        return [.. audiences];
    }

    internal IReadOnlyList<string> GetSignalRAccessTokenQueryStringPaths()
    {
        var paths = new List<string>();
        foreach (var path in this.SignalRAccessTokenQueryStringPaths)
        {
            AddIfNotEmpty(paths, path);
        }

        return paths;
    }

    internal void ThrowIfInvalid()
    {
        if (string.IsNullOrWhiteSpace(this.TenantId))
        {
            throw new InvalidOperationException(
                "Workable Entra authorization requires Workable:Entra:TenantId.");
        }

        if (this.GetAudiences().Count == 0)
        {
            throw new InvalidOperationException(
                "Workable Entra authorization requires Workable:Entra:Audience or Workable:Entra:AdditionalAudiences.");
        }

        if (!Uri.TryCreate(this.AuthorityHost, UriKind.Absolute, out var authorityHost) ||
            authorityHost.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Workable Entra authorization requires an https AuthorityHost.");
        }

        if (string.IsNullOrWhiteSpace(this.AuthenticationScheme))
        {
            throw new InvalidOperationException(
                "Workable Entra authorization requires a non-empty authentication scheme.");
        }

        if (this.AllowSignalRAccessTokensFromQueryString &&
            string.IsNullOrWhiteSpace(this.SignalRAccessTokenQueryStringName))
        {
            throw new InvalidOperationException(
                "Workable Entra authorization requires a non-empty SignalR access token query string name.");
        }

        foreach (var path in this.GetSignalRAccessTokenQueryStringPaths().Where(path => !path.StartsWith('/')))
        {
            throw new InvalidOperationException(
                "Workable Entra SignalR access token query string paths must be absolute application paths.");
        }
    }

    private static IEnumerable<string> ReadList(IConfiguration section)
    {
        foreach (var value in section.GetChildren().Select(child => child.Value?.Trim()))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static bool ParseBoolean(string? value, bool defaultValue)
        => bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;

    private static void AddIfNotEmpty(List<string> values, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            values.Add(normalized);
        }
    }

    private static void AddAcceptedAudience(ISet<string> values, string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        values.Add(normalized);
        if (TryGetPairedGuidAudience(normalized, out var pairedAudience))
        {
            values.Add(pairedAudience);
        }
    }

    private static bool TryGetPairedGuidAudience(string audience, out string pairedAudience)
    {
        const string ApiPrefix = "api://";

        if (audience.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bareAudience = audience[ApiPrefix.Length..];
            if (Guid.TryParse(bareAudience, out _))
            {
                pairedAudience = bareAudience;
                return true;
            }
        }
        else if (Guid.TryParse(audience, out _))
        {
            pairedAudience = $"{ApiPrefix}{audience}";
            return true;
        }

        pairedAudience = string.Empty;
        return false;
    }
}

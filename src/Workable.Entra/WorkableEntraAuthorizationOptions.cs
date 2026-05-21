using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace Workable;

public sealed class WorkableEntraAuthorizationOptions
{
    public string? TenantId { get; set; }

    public string? Audience { get; set; }

    public IList<string> AdditionalAudiences { get; } = [];

    public string AuthorityHost { get; set; } = WorkableEntraAuthorizationDefaults.AuthorityHost;

    public string AuthenticationScheme { get; set; } = JwtBearerDefaults.AuthenticationScheme;

    public bool MapScopesToWorkableGroups { get; set; } = true;

    public bool MapAppRolesToWorkableGroups { get; set; } = true;

    public bool MapGroupsToWorkableGroups { get; set; } = true;

    public bool AllowSignalRAccessTokensFromQueryString { get; set; } = true;

    public string SignalRAccessTokenQueryStringName { get; set; } =
        WorkableEntraAuthorizationDefaults.SignalRAccessTokenQueryStringName;

    public IList<string> SignalRAccessTokenQueryStringPaths { get; } =
        [WorkableEntraAuthorizationDefaults.SignalRHubPath];

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
        var audiences = new List<string>();
        AddIfNotEmpty(audiences, this.Audience);
        foreach (var audience in this.AdditionalAudiences)
        {
            AddIfNotEmpty(audiences, audience);
        }

        return audiences;
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
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkableEntraAuthorizationOptionsShould
{
    [Fact]
    public async Task BindSecuritySettingsFromConfigurationAtThePublicRegistrationBoundary()
    {
        using var configuration = new ConfigurationManager
        {
            ["TenantId"] = "tenant-id",
            ["Audience"] = "api://primary-audience",
            ["AuthorityHost"] = "https://login.example.test/",
            ["AuthenticationScheme"] = "WorkableBearer",
            ["MapScopesToWorkableGroups"] = "false",
            ["MapAppRolesToWorkableGroups"] = "false",
            ["MapGroupsToWorkableGroups"] = "false",
            ["AllowSignalRAccessTokensFromQueryString"] = "false",
            ["SignalRAccessTokenQueryStringName"] = "workable_token",
            ["AdditionalAudiences:0"] = " api://secondary-audience ",
            ["AdditionalAudiences:1"] = " ",
            ["SignalRAccessTokenQueryStringPaths:0"] = " /custom/realtime ",
            ["SignalRAccessTokenQueryStringPaths:1"] = "",
        };
        var services = new ServiceCollection();

        services.AddWorkableEntraAuthorization(configuration);

        await using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("WorkableBearer");
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal("https://login.example.test/tenant-id/v2.0", jwt.Authority);
        Assert.Equal("api://primary-audience", jwt.Audience);
        Assert.Equal(
            ["api://primary-audience", "api://secondary-audience"],
            jwt.TokenValidationParameters.ValidAudiences);
        Assert.Equal("WorkableBearer", workable.TransportAuthenticationScheme);
        Assert.DoesNotContain(WorkableEntraAuthorizationDefaults.ScopeClaimType, workable.GroupClaimTypes);
        Assert.DoesNotContain(WorkableEntraAuthorizationDefaults.RolesClaimType, workable.GroupClaimTypes);
        Assert.DoesNotContain(WorkableEntraAuthorizationDefaults.GroupsClaimType, workable.GroupClaimTypes);

        var context = EntraTestContext.CreateMessageReceivedContext(
            jwt,
            "WorkableBearer",
            "/custom/realtime",
            "workable_token=signalr-token");
        await jwt.Events.MessageReceived(context);
        Assert.Null(context.Token);
    }

    [Fact]
    public async Task FallBackToSecureDefaultsForInvalidBooleanConfiguration()
    {
        using var configuration = new ConfigurationManager
        {
            ["TenantId"] = "tenant-id",
            ["Audience"] = "api://primary-audience",
            ["MapScopesToWorkableGroups"] = "not-a-boolean",
            ["MapAppRolesToWorkableGroups"] = "not-a-boolean",
            ["MapGroupsToWorkableGroups"] = "not-a-boolean",
            ["AllowSignalRAccessTokensFromQueryString"] = "not-a-boolean",
        };
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(configuration);

        await using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var workable = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        Assert.Equal($"{WorkableEntraAuthorizationDefaults.AuthorityHost}/tenant-id/v2.0", jwt.Authority);
        Assert.Contains(WorkableEntraAuthorizationDefaults.ScopeClaimType, workable.GroupClaimTypes);
        Assert.Contains(WorkableEntraAuthorizationDefaults.RolesClaimType, workable.GroupClaimTypes);
        Assert.Contains(WorkableEntraAuthorizationDefaults.GroupsClaimType, workable.GroupClaimTypes);

        var context = EntraTestContext.CreateMessageReceivedContext(
            jwt,
            JwtBearerDefaults.AuthenticationScheme,
            WorkableEntraAuthorizationDefaults.SignalRHubPath,
            $"{WorkableEntraAuthorizationDefaults.SignalRAccessTokenQueryStringName}=signalr-token");
        await jwt.Events.MessageReceived(context);
        Assert.Equal("signalr-token", context.Token);
    }

    [Theory]
    [InlineData("tenant", " ", "https://login.example.test", "Bearer", true, "access_token", "/workable/realtime", "TenantId")]
    [InlineData("audience", "tenant", "https://login.example.test", "Bearer", true, "access_token", "/workable/realtime", "Audience")]
    [InlineData("authority", "tenant", "http://login.example.test", "Bearer", true, "access_token", "/workable/realtime", "https AuthorityHost")]
    [InlineData("scheme", "tenant", "https://login.example.test", " ", true, "access_token", "/workable/realtime", "authentication scheme")]
    [InlineData("query", "tenant", "https://login.example.test", "Bearer", true, " ", "/workable/realtime", "query string name")]
    [InlineData("path", "tenant", "https://login.example.test", "Bearer", true, "access_token", "relative/realtime", "absolute application paths")]
    public void RejectUnsafeOrIncompleteSecurityConfiguration(
        string caseName,
        string tenant,
        string authority,
        string scheme,
        bool allowQueryTokens,
        string queryName,
        string path,
        string expectedMessage)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = tenant;
            options.Audience = caseName == "audience" ? null : "api://target-audience";
            options.AuthorityHost = authority;
            options.AuthenticationScheme = scheme;
            options.AllowSignalRAccessTokensFromQueryString = allowQueryTokens;
            options.SignalRAccessTokenQueryStringName = queryName;
            options.SignalRAccessTokenQueryStringPaths.Clear();
            options.SignalRAccessTokenQueryStringPaths.Add(path);
        }));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermitAnEmptySignalRQueryNameWhenQueryTokensAreDisabled()
    {
        var services = new ServiceCollection();

        services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
            options.Audience = "api://target-audience";
            options.AllowSignalRAccessTokensFromQueryString = false;
            options.SignalRAccessTokenQueryStringName = " ";
        });
    }

    [Fact]
    public void GuardPublicRegistrationInputs()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkableEntraServiceCollectionExtensions.AddWorkableEntraAuthorization(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddWorkableEntraAuthorization((Action<WorkableEntraAuthorizationOptions>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddWorkableEntraAuthorization((IConfiguration)null!));
    }

    private static class EntraTestContext
    {
        public static Microsoft.AspNetCore.Authentication.JwtBearer.MessageReceivedContext CreateMessageReceivedContext(
            JwtBearerOptions options,
            string schemeName,
            string path,
            string query)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = path;
            httpContext.Request.QueryString = new QueryString($"?{query}");
            var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme(
                schemeName,
                displayName: null,
                typeof(JwtBearerHandler));
            return new(httpContext, scheme, options);
        }
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkableEntraAuthorizationTests
{
    [Fact]
    public void AspNetCoreGroupProviderCanMapEntraScopeClaimToWorkableGroups()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(WorkableEntraAuthorizationDefaults.ScopeClaimType, "Workable.Connect Workable.Read"),
                    new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Workable.Admin"),
                ],
                "Test")),
        };
        var provider = new HttpContextClaimsWorkAuthorizationGroupProvider(
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new WorkableAspNetCoreAuthorizationOptions
            {
                GroupClaimTypes =
                [
                    WorkableEntraAuthorizationDefaults.ScopeClaimType,
                    WorkableEntraAuthorizationDefaults.RolesClaimType,
                ],
                GroupClaimValueSeparators = [',', ' '],
            }));

        var groups = provider.GetGroups(new WorkActor("entra-user"), systemName: null);

        Assert.Contains(WorkableEntraAuthorizationDefaults.ConnectScope, groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.ReadScope, groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.AdminScope, groups);
    }

    [Fact]
    public void AddWorkableEntraAuthorizationFailsClosedWhenAudienceIsMissing()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
        }));

        Assert.Contains("Audience", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddWorkableEntraAuthorizationAcceptsBareGuidAudienceWhenConfiguredAudienceUsesApiUri()
    {
        const string audience = "api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
            options.Audience = audience;
        });

        await using var provider = services.BuildServiceProvider();
        var jwtOptions = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var acceptedAudiences = (jwtOptions.TokenValidationParameters.ValidAudiences ?? []).ToArray();

        Assert.Contains(audience, acceptedAudiences);
        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", acceptedAudiences);
    }

    [Fact]
    public async Task AddWorkableEntraAuthorizationAcceptsApiUriAudienceWhenConfiguredAudienceUsesBareGuid()
    {
        const string audience = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
            options.Audience = audience;
        });

        await using var provider = services.BuildServiceProvider();
        var jwtOptions = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var acceptedAudiences = (jwtOptions.TokenValidationParameters.ValidAudiences ?? []).ToArray();

        Assert.Contains(audience, acceptedAudiences);
        Assert.Contains("api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", acceptedAudiences);
    }

    [Fact]
    public async Task AddWorkableEntraAuthorizationDoesNotOverrideExistingAuthenticationDefaults()
    {
        var services = new ServiceCollection();
        services.AddAuthentication("Cookies");
        services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
            options.Audience = "api://target-app";
        });

        await using var provider = services.BuildServiceProvider();
        var authenticationOptions = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        var workableOptions = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;

        Assert.Equal("Cookies", authenticationOptions.DefaultScheme);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, workableOptions.TransportAuthenticationScheme);
    }

    [Fact]
    public async Task AddWorkableEntraAuthorizationAcceptsQueryStringAccessTokensOnlyForSignalR()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
            options.Audience = "api://target-app";
        });
        await using var provider = services.BuildServiceProvider();
        var jwtOptions = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            typeof(JwtBearerHandler));

        var signalRContext = CreateMessageReceivedContext(
            jwtOptions,
            scheme,
            "/workable/realtime",
            "signalr-token");
        await jwtOptions.Events.MessageReceived(signalRContext);

        var httpApiContext = CreateMessageReceivedContext(
            jwtOptions,
            scheme,
            "/workable/host",
            "http-token");
        await jwtOptions.Events.MessageReceived(httpApiContext);

        Assert.Equal("signalr-token", signalRContext.Token);
        Assert.Null(httpApiContext.Token);
    }

    [Fact]
    public async Task AddWorkableEntraAuthorizationPrefersAuthorizationHeaderOverSignalRQueryStringToken()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.TenantId = "tenant-id";
            options.Audience = "api://target-app";
        });
        await using var provider = services.BuildServiceProvider();
        var jwtOptions = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            typeof(JwtBearerHandler));

        var context = CreateMessageReceivedContext(
            jwtOptions,
            scheme,
            "/workable/realtime",
            "signalr-token");
        context.HttpContext.Request.Headers.Authorization = "Bearer header-token";

        await jwtOptions.Events.MessageReceived(context);

        Assert.Null(context.Token);
    }

    [Fact]
    public async Task EntraMappedClaimsAuthorizeTargetWorkableSystemThroughNormalWorkableAuthorization()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkAuthorizationGroupProvider>(_ => new FixedGroupProvider(
            [
                WorkableEntraAuthorizationDefaults.ConnectScope,
                WorkableEntraAuthorizationDefaults.ReadScope,
                WorkableEntraAuthorizationDefaults.ExecuteScope,
                WorkableEntraAuthorizationDefaults.ControlScope,
            ]));
        services.AddWorkableSystem(builder =>
        {
            builder.ConfigureAuthorization(authorization => authorization
                .AllowConnectToGroups(WorkableEntraAuthorizationDefaults.ConnectScope)
                .AllowReadAllWorkToGroups(WorkableEntraAuthorizationDefaults.ReadScope)
                .AllowOperateAllWorkToGroups(WorkableEntraAuthorizationDefaults.ExecuteScope)
                .AllowControlSystemToGroups(WorkableEntraAuthorizationDefaults.ControlScope));
            builder.AddWork(
                WorkDefinition.Create("entra.target"),
                static (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        });
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("entra-user"),
            "Verify Entra Workable target authorization.");
        var session = system.CreateSession(requestContext);

        var definition = Assert.Single(session.Catalog.Definitions);
        await system.Start(requestContext);
        var handle = await session.Queue.Enqueue(definition.Id);

        Assert.Equal("entra.target", definition.Name);
        Assert.True(handle.QueueOutcome.IsAccepted);
    }

    private sealed class FixedGroupProvider(IEnumerable<string> groups) : IWorkAuthorizationGroupProvider
    {
        private readonly IReadOnlySet<string> groups = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => this.groups;
    }

    private static MessageReceivedContext CreateMessageReceivedContext(
        JwtBearerOptions options,
        AuthenticationScheme scheme,
        string path,
        string accessToken)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Request.QueryString = new QueryString($"?access_token={accessToken}");

        return new MessageReceivedContext(httpContext, scheme, options);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkableEntraAuthorizationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddWorkableEntraAuthorizationUsesOidAndGroupsRegardlessOfInboundMapping(
        bool mapInboundClaims)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(
                JwtBearerDefaults.AuthenticationScheme,
                jwt =>
                {
                    EntraJwtTestSupport.ConfigureValidation(jwt);
                    jwt.MapInboundClaims = mapInboundClaims;
                });
        services.AddWorkableEntraAuthorization(options =>
            options.AuthenticationScheme = JwtBearerDefaults.AuthenticationScheme);

        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        httpContext.Request.Headers.Authorization = $"Bearer {EntraJwtTestSupport.CreateToken(
            new Claim("oid", EntraJwtTestSupport.ActorObjectId),
            new Claim("sub", EntraJwtTestSupport.ActorSubjectId),
            new Claim("name", "Entra User"),
            new Claim("upn", "entra.user@example.test"),
            new Claim(WorkableEntraAuthorizationDefaults.ScopeClaimType, "Workable.Read Workable.Execute"),
            new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Workable.Admin"),
            new Claim(WorkableEntraAuthorizationDefaults.GroupsClaimType, "entra-group-id"))}";

        var principal = await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(httpContext);

        Assert.NotNull(principal);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(httpContext);
        var groupProvider = Assert.Single(
            provider.GetServices<IWorkAuthorizationGroupContextProvider>());
        var groups = await groupProvider.GetCurrentGroups(actor, systemName: null);

        Assert.Equal(EntraJwtTestSupport.ActorObjectId, actor.Id);
        Assert.Equal("Entra User", actor.Name);
        Assert.Equal("entra.user@example.test", actor.Email);
        Assert.NotNull(groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.ReadScope, groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.ExecuteScope, groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.AdminScope, groups);
        Assert.Contains("entra-group-id", groups);
        Assert.Contains(
            principal.Claims,
            claim => claim.Type == (mapInboundClaims
                ? EntraJwtTestSupport.MicrosoftObjectIdClaimType
                : "oid"));
        Assert.Contains(
            principal.Claims,
            claim => claim.Type == (mapInboundClaims
                ? EntraJwtTestSupport.MicrosoftScopeClaimType
                : WorkableEntraAuthorizationDefaults.ScopeClaimType));
    }

    [Fact]
    public async Task TreatOnlyThePrincipalProducedByTheExplicitSchemeAsEntraProvenance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSchemeTestAuthentication();
        services.AddWorkableEntraAuthorization(options =>
            options.AuthenticationScheme = WorkableSchemeAuthenticationTestSupport.WorkableBearerScheme);
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Request.Headers.Authorization =
            WorkableSchemeAuthenticationTestSupport.CreateBearerHeader().ToString();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var principal = await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(context);
        var mapper = Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        var selectedIdentity = Assert.IsType<ClaimsIdentity>(principal?.Identity);

        Assert.True(mapper.TryMap(
            selectedIdentity,
            new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Billing,Operations"),
            out var groups));
        Assert.Equal(["Billing,Operations"], groups);

        var unrelatedIdentity = new ClaimsIdentity(authenticationType: "Cookies");
        Assert.False(mapper.TryMap(
            unrelatedIdentity,
            new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Admin,Operator"),
            out _));
    }

    [Fact]
    public async Task AspNetCoreGroupProviderCanMapEntraScopeClaimToWorkableGroups()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "entra-user"),
                    new Claim(WorkableEntraAuthorizationDefaults.ScopeClaimType, "Workable.Read"),
                    new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Workable.Admin"),
                ],
                "Test")),
        };
        var authorizationOptions = new WorkableAspNetCoreAuthorizationOptions
        {
            GroupClaimTypes =
            [
                WorkableEntraAuthorizationDefaults.ScopeClaimType,
                WorkableEntraAuthorizationDefaults.RolesClaimType,
            ],
            GroupClaimValueSeparators = [','],
        };
        authorizationOptions.GroupClaimValueSeparatorsByClaimType[
            WorkableEntraAuthorizationDefaults.ScopeClaimType] = [' '];
        ((ClaimsIdentity)httpContext.User.Identity!).AddClaim(
            new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Billing Admin"));
        var provider = new HttpContextClaimsWorkAuthorizationGroupProvider(
            new HttpContextAccessor { HttpContext = httpContext },
            new HttpContextWorkActorFactory(Options.Create(new WorkableAspNetCoreAuthorizationOptions())),
            Options.Create(authorizationOptions));

        var groups = await provider.GetCurrentGroups(new WorkActor("entra-user"), systemName: null);

        Assert.NotNull(groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.ReadScope, groups);
        Assert.Contains(WorkableEntraAuthorizationDefaults.AdminScope, groups);
        Assert.Contains("Billing Admin", groups);
        Assert.DoesNotContain("Billing", groups);
        Assert.DoesNotContain("Admin", groups);
    }

    [Fact]
    public async Task PreserveLiteralEntraRoleAndGroupClaimValues()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("oid", "entra-user"),
                    new Claim(WorkableEntraAuthorizationDefaults.ScopeClaimType, "Workable.Read Workable.Execute"),
                    new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Billing.Reader,Operations.Writer"),
                    new Claim(WorkableEntraAuthorizationDefaults.GroupsClaimType, "group,id"),
                ],
                "Test")),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);

        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.NotNull(groups);
        Assert.Contains("Workable.Read", groups);
        Assert.Contains("Workable.Execute", groups);
        Assert.Contains("Billing.Reader,Operations.Writer", groups);
        Assert.Contains("group,id", groups);
        Assert.DoesNotContain("Billing.Reader", groups);
        Assert.DoesNotContain("Operations.Writer", groups);
        Assert.DoesNotContain("group", groups);
        Assert.DoesNotContain("id", groups);
    }

    [Fact]
    public async Task HonorAnExplicitClaimSpecificSeparatorForAnEntraRoleClaim()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();
        services.Configure<WorkableAspNetCoreAuthorizationOptions>(options =>
        {
            options.GroupClaimValueSeparatorsByClaimType[
                WorkableEntraAuthorizationDefaults.RolesClaimType] = [';'];
            options.GroupClaimValueSeparatorsByClaimType["host_role"] = [';'];
        });

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("oid", "entra-user"),
                    new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Billing.Reader;Operations.Writer"),
                    new Claim("host_role", "Host.Reader;Host.Writer"),
                ],
                authenticationType: "Test",
                nameType: ClaimTypes.Name,
                roleType: "host_role")),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);

        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.NotNull(groups);
        Assert.Contains("Billing.Reader", groups);
        Assert.Contains("Operations.Writer", groups);
        var identity = Assert.IsType<ClaimsIdentity>(context.User.Identity);
        var mapper = Assert.Single(provider.GetServices<IWorkAuthorizationGroupClaimMapper>());
        Assert.True(mapper.TryMap(identity, identity.FindFirst("host_role")!, out var hostRoles));
        Assert.Equal(["Host.Reader", "Host.Writer"], hostRoles);
    }

    [Fact]
    public async Task PreserveTheHostsActorFallbackOrderWhenOidIsAbsent()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "host-user-id"),
                    new Claim("sub", "entra-subject-id"),
                ],
                "Test")),
        };

        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);

        Assert.Equal("host-user-id", actor.Id);
    }

    [Fact]
    public async Task AddWorkableEntraAuthorizationDoesNotOverrideExistingAuthenticationDefaults()
    {
        var services = new ServiceCollection();
        services.AddAuthentication("Cookies");
        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var authenticationOptions = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        var workableOptions = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;

        Assert.Equal("Cookies", authenticationOptions.DefaultScheme);
        Assert.Null(workableOptions.TransportAuthenticationScheme);
    }

    [Fact]
    public async Task LeaveANonEntraAmbientIdentityToHostActorAndGroupConfiguration()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "cookie-user"),
                    new Claim("preferred_username", "cookie.user@example.test"),
                    new Claim(ClaimTypes.Role, "Admin,Operator"),
                    new Claim("groups", "East,West"),
                ],
                "Cookies")),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("cookie-user", actor.Id);
        Assert.Null(actor.Email);
        Assert.NotNull(groups);
        Assert.Equal(["Admin", "East", "Operator", "West"], groups.OrderBy(group => group));
    }

    [Fact]
    public async Task DoNotTreatAnArbitraryPrincipalAsEntraBecauseATransportSchemeIsConfigured()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
            options.AuthenticationScheme = "HostEntra");

        await using var provider = services.BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "cookie-user"),
                new Claim("preferred_username", "cookie.user@example.test"),
            ],
            "Cookies"));

        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(principal);

        Assert.Equal("cookie-user", actor.Id);
        Assert.Null(actor.Email);
    }

    [Fact]
    public async Task PreserveExplicitSchemeProvenanceWhenTheHostSelectorReturnsAClonedIdentity()
    {
        var selector = new CloningIdentitySelector();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSchemeTestAuthentication();
        services.AddSingleton<IWorkClaimsIdentitySelector>(selector);
        services.AddWorkableEntraAuthorization(options =>
            options.AuthenticationScheme = WorkableSchemeAuthenticationTestSupport.WorkableBearerScheme);
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Request.Headers.Authorization =
            WorkableSchemeAuthenticationTestSupport.CreateBearerHeader().ToString();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        Assert.True(await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(context));
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("workable-user-1", actor.Id);
        Assert.NotNull(groups);
        Assert.Contains("work.read", groups);
        Assert.Contains("work.execute", groups);
        Assert.Equal(1, selector.CallCount);
    }

    [Fact]
    public async Task LetTheHostIdentifyAnAmbientEntraIdentityWithoutAnObjectId()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
            options.IdentityPredicate = identity => identity.AuthenticationType == "CustomEntra");

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "custom-entra-user"),
                    new Claim("preferred_username", "entra.user@example.test"),
                    new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Billing.Reader,Operations.Writer"),
                ],
                "CustomEntra")),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("custom-entra-user", actor.Id);
        Assert.Equal("entra.user@example.test", actor.Email);
        Assert.NotNull(groups);
        Assert.Equal(["Billing.Reader,Operations.Writer"], groups);
    }

    [Fact]
    public async Task DisabledEntraMappingsDoNotFallThroughToGenericClaimConfiguration()
    {
        const string HostRoleClaimType = "host-role";
        var services = new ServiceCollection();
        services.AddWorkableAspNetCoreAuthorization(options =>
        {
            options.GroupClaimTypes =
            [
                WorkableEntraAuthorizationDefaults.ScopeClaimType,
                WorkableEntraAuthorizationDefaults.RolesClaimType,
                WorkableEntraAuthorizationDefaults.GroupsClaimType,
                HostRoleClaimType,
                "host-group",
            ];
        });
        services.AddWorkableEntraAuthorization(options =>
        {
            options.MapScopesToWorkableGroups = false;
            options.MapAppRolesToWorkableGroups = false;
            options.MapGroupsToWorkableGroups = false;
        });

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("oid", "host-user"),
                    new Claim(WorkableEntraAuthorizationDefaults.ScopeClaimType, "Workable.Read Workable.Execute"),
                    new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "Workable.Admin"),
                    new Claim(WorkableEntraAuthorizationDefaults.GroupsClaimType, "entra-group"),
                    new Claim(HostRoleClaimType, "host-role-value"),
                    new Claim("host-group", "host-group-value"),
                ],
                "Test",
                ClaimTypes.Name,
                HostRoleClaimType)),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var workableOptions = provider.GetRequiredService<IOptions<WorkableAspNetCoreAuthorizationOptions>>().Value;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Contains(WorkableEntraAuthorizationDefaults.ScopeClaimType, workableOptions.GroupClaimTypes);
        Assert.Contains(WorkableEntraAuthorizationDefaults.RolesClaimType, workableOptions.GroupClaimTypes);
        Assert.Contains(WorkableEntraAuthorizationDefaults.GroupsClaimType, workableOptions.GroupClaimTypes);
        Assert.Contains("host-group", workableOptions.GroupClaimTypes);
        Assert.NotNull(groups);
        Assert.DoesNotContain("Workable.Read Workable.Execute", groups);
        Assert.DoesNotContain("Workable.Admin", groups);
        Assert.DoesNotContain("entra-group", groups);
        Assert.DoesNotContain("host-role-value", groups);
        Assert.Contains("host-group-value", groups);
    }

    [Fact]
    public async Task MapTheRoleClaimTypeSelectedByTheHostIdentity()
    {
        const string HostRoleClaimType = "host-app-role";
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("oid", "host-role-user"),
                    new Claim(HostRoleClaimType, "Billing.Reader,Operations.Writer"),
                ],
                "Test",
                ClaimTypes.Name,
                HostRoleClaimType)),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.NotNull(groups);
        Assert.Contains("Billing.Reader", groups);
        Assert.Contains("Operations.Writer", groups);
    }

    [Fact]
    public async Task TreatTheConcreteGroupsClaimAsGroupsBeforeTheHostRoleAlias()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization(options =>
        {
            options.MapAppRolesToWorkableGroups = true;
            options.MapGroupsToWorkableGroups = false;
        });

        await using var provider = services.BuildServiceProvider();
        var identity = new ClaimsIdentity(
            [
                new Claim("oid", "host-role-user"),
                new Claim(WorkableEntraAuthorizationDefaults.GroupsClaimType, "entra-group"),
                new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "entra-role"),
            ],
            "Test",
            ClaimTypes.Name,
            WorkableEntraAuthorizationDefaults.GroupsClaimType);
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(identity),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.NotNull(groups);
        Assert.DoesNotContain("entra-group", groups);
        Assert.Contains("entra-role", groups);
    }

    [Fact]
    public async Task PreferAHostClaimMapperAndIgnoreSecondaryIdentityClaims()
    {
        var services = new ServiceCollection();
        services.AddWorkableEntraAuthorization();
        services.AddSingleton<IWorkAuthorizationGroupClaimMapper, HostRoleClaimMapper>();

        await using var provider = services.BuildServiceProvider();
        var primary = new ClaimsIdentity(
            [
                new Claim("oid", "primary-user"),
                new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "primary-role"),
            ],
            "Primary");
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(primary),
        };
        context.User.AddIdentity(new ClaimsIdentity(
            [new Claim(WorkableEntraAuthorizationDefaults.RolesClaimType, "secondary-role")],
            "Secondary"));
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("primary-user", actor.Id);
        Assert.NotNull(groups);
        Assert.Equal(["host:primary-role"], groups);
    }

    [Fact]
    public async Task EntraMappedClaimsAuthorizeTargetWorkableSystemThroughNormalWorkableAuthorization()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkAuthorizationGroupProvider>(_ => new FixedGroupProvider(
            [
                WorkableEntraAuthorizationDefaults.ReadScope,
                WorkableEntraAuthorizationDefaults.ExecuteScope,
                WorkableEntraAuthorizationDefaults.ControlScope,
            ]));
        services.AddWorkableSystem(builder =>
        {
            builder.ConfigureAuthorization(authorization => authorization
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
        var session = await system.CreateSession(requestContext);

        var definition = Assert.Single(session.Catalog.Definitions);
        await system.Start(requestContext);
        var handle = await session.Queue.Enqueue(definition.Name);

        Assert.Equal("entra.target", definition.Name);
        Assert.True(handle.QueueOutcome.IsAccepted);
    }

    private sealed class FixedGroupProvider(IEnumerable<string> groups) : IWorkAuthorizationGroupProvider
    {
        private readonly IReadOnlySet<string> groups = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(this.groups);
    }

    private sealed class HostRoleClaimMapper : IWorkAuthorizationGroupClaimMapper
    {
        public bool TryMap(
            ClaimsIdentity identity,
            Claim claim,
            out IReadOnlyList<string> groups)
        {
            if (string.Equals(
                    claim.Type,
                    WorkableEntraAuthorizationDefaults.RolesClaimType,
                    StringComparison.OrdinalIgnoreCase))
            {
                groups = [$"host:{claim.Value}"];
                return true;
            }

            groups = [];
            return false;
        }
    }

    private sealed class CloningIdentitySelector : IWorkClaimsIdentitySelector
    {
        public int CallCount { get; private set; }

        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal)
        {
            this.CallCount++;
            return principal.Identity is ClaimsIdentity identity
                ? new ClaimsIdentity(identity)
                : null;
        }
    }

}

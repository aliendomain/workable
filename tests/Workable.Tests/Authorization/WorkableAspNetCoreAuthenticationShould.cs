using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkableAspNetCoreAuthenticationShould
{
    [Fact]
    public async Task InvokeTheHostsDefaultChallengeWhenNoTransportSchemeIsSelected()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSchemeTestAuthentication();
        services.PostConfigure<WorkableAspNetCoreAuthorizationOptions>(options =>
            options.TransportAuthenticationScheme = null);
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };

        Assert.True(await WorkableAspNetCoreAuthentication.ChallengeAsync(context));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            WorkableSchemeAuthenticationTestSupport.AmbientScheme,
            context.Response.Headers[WorkableSchemeChallengeProbe.HeaderName]);
    }

    [Fact]
    public async Task ReportNoChallengeWhenTheHostHasNoAuthenticationSchemes()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        Assert.False(await WorkableAspNetCoreAuthentication.ChallengeAsync(context));
        Assert.False(await WorkableAspNetCoreAuthentication.ChallengeAsync(httpContext: null));
        Assert.False(await WorkableAspNetCoreAuthentication.ChallengeAsync(new DefaultHttpContext()));
    }

    [Fact]
    public void GuardActorFactoryDependenciesAndResolveEveryPrincipalShape()
    {
        var options = Options.Create(new WorkableAspNetCoreAuthorizationOptions
        {
            ActorIdClaimTypes = ["actor-id", "fallback-id"],
            ActorNameClaimTypes = ["actor-name"],
            ActorEmailClaimTypes = ["actor-email"],
        });
        Assert.Throws<ArgumentNullException>(() => new HttpContextWorkActorFactory(null!));
        Assert.Throws<ArgumentNullException>(() => new HttpContextWorkActorFactory(
            options,
            claimMappers: null!,
            new PrimaryWorkClaimsIdentitySelector()));
        Assert.Throws<ArgumentNullException>(() => new HttpContextWorkActorFactory(
            options,
            [],
            identitySelector: null!));

        var factory = new HttpContextWorkActorFactory(options);
        Assert.Same(WorkActor.Unknown, factory.Create((ClaimsPrincipal?)null));
        Assert.Same(WorkActor.Unknown, factory.Create(new ClaimsPrincipal(new ClaimsIdentity())));
        var identity = new ClaimsIdentity(
            [
                new Claim("actor-id", " "),
                new Claim("fallback-id", "resolved-id"),
                new Claim("actor-name", "Resolved Name"),
                new Claim("actor-email", "resolved@example.test"),
            ],
            "Test");
        var actor = factory.Create(new ClaimsPrincipal(identity));
        Assert.Equal("resolved-id", actor.Id);
        Assert.Equal("Resolved Name", actor.Name);
        Assert.Equal("resolved@example.test", actor.Email);

        var createIdentity = typeof(HttpContextWorkActorFactory).GetMethod(
            "Create",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(ClaimsIdentity)]);
        Assert.NotNull(createIdentity);
        Assert.Same(WorkActor.Unknown, createIdentity.Invoke(factory, [null]));
    }

    [Fact]
    public void ExposeCurrentPrincipalAndIdentityOnlyForAnAuthenticatedSnapshot()
    {
        Assert.Null(WorkableAspNetCoreAuthentication.GetCurrentPrincipal(null));
        Assert.Null(WorkableAspNetCoreAuthentication.GetCurrentIdentity(null));
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "current-user")],
            "Test");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };

        Assert.Same(context.User, WorkableAspNetCoreAuthentication.GetCurrentPrincipal(context));
        Assert.Same(identity, WorkableAspNetCoreAuthentication.GetCurrentIdentity(context));
    }

    [Fact]
    public void MatchCurrentIdentityAgainstActiveAndHttpContextSnapshots()
    {
        var activeIdentity = new ClaimsIdentity(authenticationType: "Active");
        var contextIdentity = new ClaimsIdentity(authenticationType: "Context");
        var unrelated = new ClaimsIdentity(authenticationType: "Other");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(contextIdentity),
        };
        var active = new WorkableAspNetCoreAuthentication.WorkableAuthenticationSnapshot(
            new ClaimsPrincipal(activeIdentity),
            activeIdentity,
            authenticationScheme: null);

        using (WorkableAspNetCoreAuthentication.UseSnapshot(active))
        {
            Assert.True(WorkableAspNetCoreAuthentication.IsCurrentIdentity(context, activeIdentity));
            Assert.True(WorkableAspNetCoreAuthentication.IsCurrentIdentity(context, contextIdentity));
            Assert.False(WorkableAspNetCoreAuthentication.IsCurrentIdentity(context, unrelated));
        }
    }

    [Fact]
    public async Task ReportNoChallengeWhenTheHostHasNoDefaultChallengeScheme()
    {
        var services = new ServiceCollection();
        services.AddAuthentication();
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };

        Assert.False(await WorkableAspNetCoreAuthentication.ChallengeAsync(context));
    }

    [Fact]
    public async Task KeepAnExplicitSchemeUnauthenticatedUntilItIsEvaluated()
    {
        var services = new ServiceCollection();
        services.AddWorkableAspNetCoreAuthorization(options =>
            options.TransportAuthenticationScheme = "HostScheme");
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ambient-user")],
                "Ambient")),
        };

        Assert.False(WorkableAspNetCoreAuthentication.IsAuthenticated(context));
        Assert.False(await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext: null));
        Assert.Null(await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(httpContext: null));
    }

    [Fact]
    public async Task RestoreThePriorActiveSnapshotWithoutOverridingAnExplicitContext()
    {
        var activeIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "active-user")],
            "Active");
        var activeSnapshot = new WorkableAspNetCoreAuthentication.WorkableAuthenticationSnapshot(
            new ClaimsPrincipal(activeIdentity),
            activeIdentity,
            authenticationScheme: null);
        var explicitPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "explicit-user")],
            "Explicit"));
        var explicitContext = new DefaultHttpContext
        {
            User = explicitPrincipal,
        };
        var scope = WorkableAspNetCoreAuthentication.UseSnapshot(activeSnapshot);

        Assert.Same(activeSnapshot, WorkableAspNetCoreAuthentication.GetActiveSnapshot());
        Assert.Same(
            explicitPrincipal,
            await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(explicitContext));
        scope.Dispose();
        scope.Dispose();

        Assert.Null(WorkableAspNetCoreAuthentication.GetActiveSnapshot());
    }

    [Fact]
    public async Task SelectOneIdentitySnapshotForAuthenticationActorAndGroups()
    {
        var selector = new CloningIdentitySelector();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkClaimsIdentitySelector>(selector);
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "snapshot-user"),
                    new Claim("groups", "snapshot-group"),
                ],
                "Host")),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        Assert.True(await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(context));
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("snapshot-user", actor.Id);
        Assert.NotNull(groups);
        Assert.Equal(["snapshot-group"], groups);
        Assert.Equal(1, selector.CallCount);
    }

    [Fact]
    public async Task FreezeAHostActorFactoryOnceWhilePreparingClaimsGroups()
    {
        var actors = new CountingActorFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkActorFactory>(actors);
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("groups", "snapshot-group")],
                "Host")),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        Assert.True(await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(context));
        await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(context);

        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actors.Actor, systemName: null);

        Assert.Equal(1, actors.CallCount);
        Assert.NotNull(groups);
        Assert.Equal(["snapshot-group"], groups);
        Assert.Null(await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(new WorkActor("different-actor", null, null), systemName: null));
    }

    [Fact]
    public async Task IgnoreAuthorizationSnapshotPreparationForAnAnonymousRequest()
    {
        var services = new ServiceCollection();
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };

        await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(context);

        Assert.False(WorkableAspNetCoreAuthentication.IsAuthenticated(context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeepTheSelectedPrincipalPrivateToWorkable(bool provideWorkableToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSchemeTestAuthentication();
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var ambientPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ambient-user")],
            WorkableSchemeAuthenticationTestSupport.AmbientScheme));
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = ambientPrincipal,
        };
        if (provideWorkableToken)
        {
            context.Request.Headers.Authorization =
                WorkableSchemeAuthenticationTestSupport.CreateBearerHeader().ToString();
        }

        var selected = await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(context);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);

        Assert.Same(ambientPrincipal, context.User);
        Assert.Equal(provideWorkableToken, selected is not null);
        Assert.Equal(provideWorkableToken ? "workable-user-1" : null, actor.Id);

        var groupProvider = Assert.Single(
            provider.GetServices<IWorkAuthorizationGroupContextProvider>());
        var groups = await groupProvider.GetCurrentGroups(actor, systemName: null);
        if (provideWorkableToken)
        {
            Assert.NotNull(groups);
            Assert.Contains(TransportAuthorizationTestSupport.ReadGroups.First(), groups);
        }
        else
        {
            Assert.Null(groups);
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

    private sealed class CountingActorFactory : IWorkActorFactory
    {
        public WorkActor Actor { get; } = new("host-actor", "Host Actor", null);

        public int CallCount { get; private set; }

        public WorkActor Create(HttpContext? httpContext)
        {
            this.CallCount++;
            return this.Actor;
        }

        public WorkActor Create(ClaimsPrincipal? user)
        {
            this.CallCount++;
            return this.Actor;
        }
    }
}

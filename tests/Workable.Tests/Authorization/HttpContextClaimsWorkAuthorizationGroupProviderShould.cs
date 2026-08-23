using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class HttpContextClaimsWorkAuthorizationGroupProviderShould
{
    [Fact]
    public void RejectNullConstructorDependencies()
    {
        var accessor = new HttpContextAccessor();
        var options = Options.Create(new WorkableAspNetCoreAuthorizationOptions());
        var actors = new HttpContextWorkActorFactory(options);
        var selector = new PrimaryWorkClaimsIdentitySelector();
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            null!, actors, options, [], selector));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            accessor, null!, options, [], selector));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            accessor, actors, null!, [], selector));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            accessor, actors, options, null!, selector));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            accessor, actors, options, [], null!));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            null!, options, services));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            accessor, null!, services));
        Assert.Throws<ArgumentNullException>(() => new HttpContextClaimsWorkAuthorizationGroupProvider(
            accessor, options, null!));
    }

    [Fact]
    public async Task ReturnUnresolvedWhenHttpContextIsMissing()
    {
        var provider = CreateProvider(new HttpContextAccessor());

        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.Null(groups);
    }

    [Fact]
    public async Task ReturnUnresolvedWhenUserIsNotAuthenticated()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.Null(groups);
    }

    [Fact]
    public async Task ReturnUnresolvedWhenCurrentUserDoesNotMatchActor()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "http-group")),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var groups = await provider.GetCurrentGroups(new WorkActor("different-user"), systemName: null);

        Assert.Null(groups);
    }

    [Fact]
    public async Task SplitTrimAndDeduplicateConfiguredGroupClaims()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(
                new Claim("GROUPS", "operations, billing ; operations"),
                new Claim("roles", "ignored")),
        };
        var provider = CreateProvider(
            new HttpContextAccessor { HttpContext = context },
            new WorkableAspNetCoreAuthorizationOptions
            {
                GroupClaimTypes = ["groups"],
                GroupClaimValueSeparators = [',', ';'],
            });

        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.NotNull(groups);
        Assert.Equal(["billing", "operations"], Sort(groups));
    }

    [Fact]
    public async Task KeepOneAuthenticationSnapshotAcrossSystemScopedGroupCacheEntries()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "default-group")),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var firstDefaultGroups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);
        context.User = CreateUser(new Claim("groups", "named-group"));
        var cachedDefaultGroups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);
        var namedGroups = await provider.GetCurrentGroups(new WorkActor("user"), "background");

        Assert.NotNull(firstDefaultGroups);
        Assert.NotNull(cachedDefaultGroups);
        Assert.NotNull(namedGroups);
        Assert.Equal(["default-group"], Sort(firstDefaultGroups));
        Assert.Equal(["default-group"], Sort(cachedDefaultGroups));
        Assert.Equal(["default-group"], Sort(namedGroups));
    }

    [Fact]
    public async Task KeepAnExplicitContextAuthoritativeWhileAnotherGroupSnapshotIsActive()
    {
        var activeIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "active-user")],
            "Active");
        var activeSnapshot = new WorkableAspNetCoreAuthentication.WorkableAuthenticationSnapshot(
            new ClaimsPrincipal(activeIdentity),
            activeIdentity,
            authenticationScheme: null)
        {
            Actor = new WorkActor("active-user"),
            ClaimsGroups = new HashSet<string>(["active-group"], StringComparer.OrdinalIgnoreCase),
        };
        var explicitContext = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "explicit-group")),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = explicitContext });

        using var scope = WorkableAspNetCoreAuthentication.UseSnapshot(activeSnapshot);
        var groups = await provider.GetCurrentGroups(
            explicitContext,
            new WorkActor("user"),
            systemName: null);

        Assert.NotNull(groups);
        Assert.Equal(["explicit-group"], Sort(groups));
    }

    [Fact]
    public async Task DoNotResolveGroupsFromAmbientContextWhileAnUnresolvedSnapshotIsActive()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "ambient-group")),
        };
        var activeIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user")],
            "Active");
        var activeSnapshot = new WorkableAspNetCoreAuthentication.WorkableAuthenticationSnapshot(
            new ClaimsPrincipal(activeIdentity),
            activeIdentity,
            authenticationScheme: null)
        {
            Actor = new WorkActor("user"),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        using var scope = WorkableAspNetCoreAuthentication.UseSnapshot(activeSnapshot);
        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.Null(groups);
        Assert.Null(activeSnapshot.ClaimsGroups);
    }

    [Fact]
    public async Task ReturnUnresolvedWhenTheHostIdentitySelectorRejectsThePrincipal()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "ignored")),
        };
        var options = Options.Create(new WorkableAspNetCoreAuthorizationOptions());
        var provider = new HttpContextClaimsWorkAuthorizationGroupProvider(
            new HttpContextAccessor { HttpContext = context },
            new HttpContextWorkActorFactory(options),
            options,
            [],
            new RejectingIdentitySelector());

        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.Null(groups);
    }

    [Fact]
    public async Task ReadGroupsOnlyFromTheSelectedAuthenticatedIdentity()
    {
        var primary = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "user"),
                new Claim("groups", "primary-group"),
            ],
            "Primary");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(primary),
        };
        context.User.AddIdentity(new ClaimsIdentity(
            [new Claim("groups", "secondary-group")],
            authenticationType: null));
        context.User.AddIdentity(new ClaimsIdentity(
            [new Claim("groups", "other-authenticated-group")],
            "Secondary"));
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.NotNull(groups);
        Assert.Equal(["primary-group"], Sort(groups));
    }

    [Fact]
    public async Task RunHostClaimMappersBeforeIntegrationDefaultsRegardlessOfRegistrationOrder()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(new Claim("roles", "package-value")),
        };
        var options = Options.Create(new WorkableAspNetCoreAuthorizationOptions());
        var provider = new HttpContextClaimsWorkAuthorizationGroupProvider(
            new HttpContextAccessor { HttpContext = context },
            new HttpContextWorkActorFactory(options),
            options,
            [new PackageMapper(), new HostMapper()]);

        var groups = await provider.GetCurrentGroups(new WorkActor("user"), systemName: null);

        Assert.NotNull(groups);
        Assert.Equal(["host-value"], Sort(groups));
    }

    [Fact]
    public async Task UseTheSameHostSelectedIdentityForActorAndGroups()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkClaimsIdentitySelector>(
            new AuthenticationTypeSelector("Workable"));
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "ambient-user"),
                new Claim("groups", "ambient-group"),
            ],
            "Ambient"));
        user.AddIdentity(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "workable-user"),
                new Claim("groups", "workable-group"),
            ],
            "Workable"));
        var context = new DefaultHttpContext { User = user, RequestServices = provider };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var actor = provider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("workable-user", actor.Id);
        Assert.NotNull(groups);
        Assert.Equal(["workable-group"], Sort(groups));
    }

    [Fact]
    public async Task SupportRequestScopedHostIdentitySelectorsAndClaimMappers()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWorkClaimsIdentitySelector, ScopedIdentitySelector>();
        services.AddScoped<IWorkActorClaimsMapper, ScopedActorMapper>();
        services.AddScoped<IWorkAuthorizationGroupClaimMapper, ScopedGroupMapper>();
        services.AddWorkableAspNetCoreAuthorization();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        await using var scope = provider.CreateAsyncScope();
        var identity = new ClaimsIdentity(
            [new Claim("host-group", "scoped-group")],
            "Scoped");
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(identity),
        };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var actor = scope.ServiceProvider.GetRequiredService<IWorkActorFactory>().Create(context);
        var groups = await Assert.Single(
                provider.GetServices<IWorkAuthorizationGroupContextProvider>())
            .GetCurrentGroups(actor, systemName: null);

        Assert.Equal("scoped-actor", actor.Id);
        Assert.NotNull(groups);
        Assert.Equal(["scoped:scoped-group"], Sort(groups));
    }

    [Fact]
    public async Task ComposeHttpClaimsWithActorProviderForBackgroundResolution()
    {
        var actorProvider = new TrackingActorGroupProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkAuthorizationGroupProvider>(actorProvider);
        services.AddWorkableAspNetCoreAuthorization();
        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        using var serviceProvider = services.BuildServiceProvider();
        var accessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "http-group")),
        };
        var resolver = serviceProvider.GetRequiredService<IWorkAuthorizationGroupResolver>();

        var httpGroups = await resolver.GetGroups(
            WorkRequestContext.Create(
                WorkInvocationChannel.HttpApi,
                new WorkActor("user"),
                isAuthenticated: true),
            systemName: null);
        accessor.HttpContext = null;
        var backgroundGroups = await resolver.GetGroups(
            WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                new WorkActor("durable-user"),
                isAuthenticated: true),
            "background");

        Assert.Equal(["http-group"], Sort(httpGroups));
        Assert.Equal(["background-group"], Sort(backgroundGroups));
        Assert.Equal([("durable-user", "background")], actorProvider.Calls);
    }

    [Fact]
    public async Task PreferAHostContextProviderRegisteredAfterTheAspNetCoreDefault()
    {
        var services = new ServiceCollection();
        services.AddWorkableAspNetCoreAuthorization();
        services.AddSingleton<IWorkAuthorizationGroupContextProvider>(new HostContextGroupProvider());
        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        using var serviceProvider = services.BuildServiceProvider();
        var accessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "default-claims-group")),
        };

        var groups = await serviceProvider
            .GetRequiredService<IWorkAuthorizationGroupResolver>()
            .GetGroups(
                WorkRequestContext.Create(
                    WorkInvocationChannel.HttpApi,
                    new WorkActor("user"),
                    isAuthenticated: true),
                systemName: null);

        Assert.Equal(["host-context-group"], Sort(groups));
    }

    private static HttpContextClaimsWorkAuthorizationGroupProvider CreateProvider(
        IHttpContextAccessor accessor,
        WorkableAspNetCoreAuthorizationOptions? options = null)
    {
        var resolvedOptions = options ?? new WorkableAspNetCoreAuthorizationOptions();
        var optionValue = Options.Create(resolvedOptions);
        return new(accessor, new HttpContextWorkActorFactory(optionValue), optionValue);
    }

    private static ClaimsPrincipal CreateUser(params Claim[] claims)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user"), .. claims],
            authenticationType: "Test"));

    private static string[] Sort(IReadOnlySet<string> groups)
        => groups
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed class TrackingActorGroupProvider : IWorkAuthorizationGroupProvider
    {
        public List<(string? ActorId, string? SystemName)> Calls { get; } = [];

        public async ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            this.Calls.Add((actor.Id, systemName));
            return new HashSet<string>(["background-group"], StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class HostContextGroupProvider : IWorkAuthorizationGroupContextProvider
    {
        public ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlySet<string>?>(
                new HashSet<string>(["host-context-group"], StringComparer.OrdinalIgnoreCase));
    }

    private sealed class HostMapper : IWorkAuthorizationGroupClaimMapper
    {
        public bool TryMap(ClaimsIdentity identity, Claim claim, out IReadOnlyList<string> groups)
        {
            groups = claim.Type == "roles" ? ["host-value"] : [];
            return claim.Type == "roles";
        }
    }

    private sealed class PackageMapper : IWorkAuthorizationGroupClaimMapper
    {
        public int Order => 1000;

        public bool TryMap(ClaimsIdentity identity, Claim claim, out IReadOnlyList<string> groups)
        {
            groups = claim.Type == "roles" ? ["package-value"] : [];
            return claim.Type == "roles";
        }
    }

    private sealed class AuthenticationTypeSelector(string authenticationType)
        : IWorkClaimsIdentitySelector
    {
        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal)
            => principal.Identities.FirstOrDefault(identity =>
                identity.IsAuthenticated &&
                string.Equals(identity.AuthenticationType, authenticationType, StringComparison.Ordinal));
    }

    private sealed class RejectingIdentitySelector : IWorkClaimsIdentitySelector
    {
        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal) => null;
    }

    private sealed class ScopedIdentitySelector : IWorkClaimsIdentitySelector
    {
        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal)
            => principal.Identities.Single(identity => identity.AuthenticationType == "Scoped");
    }

    private sealed class ScopedActorMapper : IWorkActorClaimsMapper
    {
        public bool TryCreate(ClaimsIdentity identity, out WorkActor actor)
        {
            actor = new WorkActor("scoped-actor");
            return true;
        }
    }

    private sealed class ScopedGroupMapper : IWorkAuthorizationGroupClaimMapper
    {
        public bool TryMap(ClaimsIdentity identity, Claim claim, out IReadOnlyList<string> groups)
        {
            groups = claim.Type == "host-group" ? [$"scoped:{claim.Value}"] : [];
            return claim.Type == "host-group";
        }
    }
}

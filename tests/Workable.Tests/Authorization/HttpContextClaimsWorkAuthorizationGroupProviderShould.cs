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
    public async Task CacheGroupsSeparatelyBySystemName()
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
        Assert.Equal(["named-group"], Sort(namedGroups));
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
}

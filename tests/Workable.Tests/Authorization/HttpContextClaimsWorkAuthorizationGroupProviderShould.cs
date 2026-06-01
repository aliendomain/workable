using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class HttpContextClaimsWorkAuthorizationGroupProviderShould
{
    [Fact]
    public void ReturnEmptyGroupsWhenHttpContextIsMissing()
    {
        var provider = CreateProvider(new HttpContextAccessor());

        var groups = provider.GetGroups(new WorkActor("user"), systemName: null);

        Assert.Empty(groups);
    }

    [Fact]
    public void ReturnEmptyGroupsWhenUserIsNotAuthenticated()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var groups = provider.GetGroups(new WorkActor("user"), systemName: null);

        Assert.Empty(groups);
    }

    [Fact]
    public void SplitTrimAndDeduplicateConfiguredGroupClaims()
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

        var groups = provider.GetGroups(new WorkActor("user"), systemName: null);

        Assert.Equal(["billing", "operations"], Sort(groups));
    }

    [Fact]
    public void CacheGroupsSeparatelyBySystemName()
    {
        var context = new DefaultHttpContext
        {
            User = CreateUser(new Claim("groups", "default-group")),
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var firstDefaultGroups = provider.GetGroups(new WorkActor("user"), systemName: null);
        context.User = CreateUser(new Claim("groups", "named-group"));
        var cachedDefaultGroups = provider.GetGroups(new WorkActor("user"), systemName: null);
        var namedGroups = provider.GetGroups(new WorkActor("user"), "background");

        Assert.Equal(["default-group"], Sort(firstDefaultGroups));
        Assert.Equal(["default-group"], Sort(cachedDefaultGroups));
        Assert.Equal(["named-group"], Sort(namedGroups));
    }

    private static HttpContextClaimsWorkAuthorizationGroupProvider CreateProvider(
        IHttpContextAccessor accessor,
        WorkableAspNetCoreAuthorizationOptions? options = null)
        => new(accessor, Options.Create(options ?? new WorkableAspNetCoreAuthorizationOptions()));

    private static ClaimsPrincipal CreateUser(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static string[] Sort(IReadOnlySet<string> groups)
        => groups
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

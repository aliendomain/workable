using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class HttpContextWorkActorFactoryShould
{
    [Fact]
    public void ReturnUnknownActorWhenHttpContextIsMissing()
    {
        var factory = CreateFactory();

        var actor = factory.Create((HttpContext?)null);

        Assert.Equal(WorkActor.Unknown, actor);
    }

    [Fact]
    public void ReturnUnknownActorWhenUserIsNotAuthenticated()
    {
        var factory = CreateFactory();
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        var actor = factory.Create(user);

        Assert.Equal(WorkActor.Unknown, actor);
    }

    [Fact]
    public void CreateActorFromDefaultAuthenticatedClaims()
    {
        var factory = CreateFactory();
        var user = CreateUser(
            new Claim(ClaimTypes.NameIdentifier, "user-123"),
            new Claim(ClaimTypes.Name, "Greya"),
            new Claim(ClaimTypes.Email, "greya@example.test"));

        var actor = factory.Create(user);

        Assert.Equal("user-123", actor.Id);
        Assert.Equal("Greya", actor.Name);
        Assert.Equal("greya@example.test", actor.Email);
    }

    [Fact]
    public void CreateActorFromConfiguredClaimsWhenDefaultsAreMissing()
    {
        var factory = CreateFactory(new WorkableAspNetCoreAuthorizationOptions
        {
            ActorIdClaimTypes = ["oid"],
            ActorNameClaimTypes = ["preferred_name"],
            ActorEmailClaimTypes = ["mail"],
        });
        var user = CreateUser(
            new Claim("oid", "custom-user"),
            new Claim("preferred_name", "Custom Name"),
            new Claim("mail", "custom@example.test"));

        var actor = factory.Create(user);

        Assert.Equal("custom-user", actor.Id);
        Assert.Equal("Custom Name", actor.Name);
        Assert.Equal("custom@example.test", actor.Email);
    }

    [Fact]
    public void PreferIdentityNameOverConfiguredNameClaims()
    {
        var factory = CreateFactory(new WorkableAspNetCoreAuthorizationOptions
        {
            ActorNameClaimTypes = ["preferred_name"],
        });
        var user = CreateUser(
            new Claim(ClaimTypes.Name, "Identity Name"),
            new Claim("preferred_name", "Preferred Name"));

        var actor = factory.Create(user);

        Assert.Equal("Identity Name", actor.Name);
    }

    private static HttpContextWorkActorFactory CreateFactory(
        WorkableAspNetCoreAuthorizationOptions? options = null)
        => new(Options.Create(options ?? new WorkableAspNetCoreAuthorizationOptions()));

    private static ClaimsPrincipal CreateUser(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));
}

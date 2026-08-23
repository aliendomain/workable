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

    [Fact]
    public void ResolveEveryActorFieldFromOneAuthenticatedIdentity()
    {
        var factory = CreateFactory(new WorkableAspNetCoreAuthorizationOptions
        {
            ActorIdClaimTypes = ["oid"],
            ActorNameClaimTypes = ["display-name"],
            ActorEmailClaimTypes = ["mail"],
        });
        var primary = new ClaimsIdentity(
            [
                new Claim("oid", "primary-user"),
                new Claim("display-name", "Primary User"),
            ],
            "Primary");
        var user = new ClaimsPrincipal(primary);
        user.AddIdentity(new ClaimsIdentity(
            [
                new Claim("oid", "secondary-user"),
                new Claim("mail", "secondary@example.test"),
            ],
            "Secondary"));

        var actor = factory.Create(user);

        Assert.Equal("primary-user", actor.Id);
        Assert.Equal("Primary User", actor.Name);
        Assert.Null(actor.Email);
    }

    [Fact]
    public void AllowTheHostToSelectOneAuthenticatedIdentity()
    {
        var options = Options.Create(new WorkableAspNetCoreAuthorizationOptions());
        var factory = new HttpContextWorkActorFactory(
            options,
            new AuthenticationTypeIdentitySelector("Workable"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ambient-user")],
            "Ambient"));
        user.AddIdentity(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "workable-user")],
            "Workable"));

        var actor = factory.Create(user);

        Assert.Equal("workable-user", actor.Id);
    }

    [Fact]
    public void RejectAnUnauthenticatedIdentityReturnedByAHostSelector()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "untrusted-user")]);
        var factory = new HttpContextWorkActorFactory(
            Options.Create(new WorkableAspNetCoreAuthorizationOptions()),
            new FixedIdentitySelector(identity));

        var actor = factory.Create(new ClaimsPrincipal(identity));

        Assert.Equal(WorkActor.Unknown, actor);
    }

    [Fact]
    public void RunHostActorClaimMappersBeforeIntegrationDefaults()
    {
        var factory = new HttpContextWorkActorFactory(
            Options.Create(new WorkableAspNetCoreAuthorizationOptions()),
            [new PackageActorMapper(), new HostActorMapper()],
            new PrimaryIdentitySelector());

        var actor = factory.Create(CreateUser(new Claim(ClaimTypes.NameIdentifier, "generic-user")));

        Assert.Equal("host-user", actor.Id);
    }

    [Fact]
    public void GuardActorClaimMapperCollection()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpContextWorkActorFactory(
            Options.Create(new WorkableAspNetCoreAuthorizationOptions()),
            null!,
            new PrimaryIdentitySelector()));
    }

    private static HttpContextWorkActorFactory CreateFactory(
        WorkableAspNetCoreAuthorizationOptions? options = null)
        => new(Options.Create(options ?? new WorkableAspNetCoreAuthorizationOptions()));

    private static ClaimsPrincipal CreateUser(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private sealed class AuthenticationTypeIdentitySelector(string authenticationType)
        : IWorkClaimsIdentitySelector
    {
        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal)
            => principal.Identities.FirstOrDefault(identity =>
                identity.IsAuthenticated &&
                string.Equals(identity.AuthenticationType, authenticationType, StringComparison.Ordinal));
    }

    private sealed class FixedIdentitySelector(ClaimsIdentity identity) : IWorkClaimsIdentitySelector
    {
        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal) => identity;
    }

    private sealed class PrimaryIdentitySelector : IWorkClaimsIdentitySelector
    {
        public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal)
            => principal.Identity as ClaimsIdentity;
    }

    private sealed class HostActorMapper : IWorkActorClaimsMapper
    {
        public bool TryCreate(ClaimsIdentity identity, out WorkActor actor)
        {
            actor = new WorkActor("host-user");
            return true;
        }
    }

    private sealed class PackageActorMapper : IWorkActorClaimsMapper
    {
        public int Order => 1000;

        public bool TryCreate(ClaimsIdentity identity, out WorkActor actor)
        {
            actor = new WorkActor("package-user");
            return true;
        }
    }
}

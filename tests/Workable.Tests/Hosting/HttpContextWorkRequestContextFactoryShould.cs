using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class HttpContextWorkRequestContextFactoryShould
{
    [Fact]
    public void CreateUnknownActorContextWithoutUrlWhenHttpContextIsMissing()
    {
        var actors = new RecordingActorFactory(WorkActor.Unknown);
        var factory = new HttpContextWorkRequestContextFactory(actors);

        var context = factory.Create(
            httpContext: null,
            WorkInvocationChannel.HttpApi,
            "Missing HTTP context.");

        Assert.Null(actors.LastHttpContext);
        Assert.Equal(WorkActor.Unknown, context.Actor);
        Assert.False(context.IsAuthenticated);
        Assert.Equal(WorkInvocationChannel.HttpApi, context.Channel);
        Assert.Equal(WorkOriginSurface.HostApplication, context.Surface);
        Assert.Equal("Missing HTTP context.", context.Description);
        Assert.Null(context.Url);
    }

    [Fact]
    public void CreateRequestContextWithNullDescriptionByDefault()
    {
        var actor = new WorkActor("user-123", "Test User", "user@example.test");
        var actors = new RecordingActorFactory(actor);
        var factory = new HttpContextWorkRequestContextFactory(actors);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/custom/queue";

        var context = factory.Create(
            httpContext,
            WorkInvocationChannel.HttpApi);

        Assert.Same(httpContext, actors.LastHttpContext);
        Assert.Equal(actor, context.Actor);
        Assert.False(context.IsAuthenticated);
        Assert.Equal(WorkInvocationChannel.HttpApi, context.Channel);
        Assert.Equal(WorkOriginSurface.HostApplication, context.Surface);
        Assert.Null(context.Description);
        Assert.Equal("/custom/queue", context.Url);
    }

    [Fact]
    public void CreateRequestContextFromActorFactoryAndHttpRequestUrl()
    {
        var actor = new WorkActor("user-123", "Test User", "user@example.test");
        var actors = new RecordingActorFactory(actor);
        var factory = new HttpContextWorkRequestContextFactory(actors);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-123")],
            authenticationType: "Test"));
        httpContext.Request.PathBase = "/workable";
        httpContext.Request.Path = "/custom/queue";
        httpContext.Request.QueryString = new QueryString("?definition=demo");

        var context = factory.Create(
            httpContext,
            WorkInvocationChannel.HttpApi,
            "Queue through HTTP.");

        Assert.Same(httpContext, actors.LastHttpContext);
        Assert.Equal(actor, context.Actor);
        Assert.Equal(actor, context.Origin.Actor);
        Assert.True(context.IsAuthenticated);
        Assert.Equal(WorkInvocationChannel.HttpApi, context.Channel);
        Assert.Equal(WorkOriginSurface.HostApplication, context.Surface);
        Assert.Equal("Queue through HTTP.", context.Description);
        Assert.Equal("/workable/custom/queue", context.Url);
        Assert.DoesNotContain("demo", context.Url!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludeQueryStringFromEveryProtocolRequestContextUrl()
    {
        var actors = new RecordingActorFactory(new WorkActor("user-123"));
        var factory = new HttpContextWorkRequestContextFactory(actors);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/host";
        httpContext.Request.Path = "/workable/realtime";
        httpContext.Request.QueryString = new QueryString(
            "?access_token=secret-bearer-token&id=connection-id");

        foreach (var channel in new[]
        {
            WorkInvocationChannel.HttpApi,
            WorkInvocationChannel.SignalR,
            WorkInvocationChannel.Mcp,
        })
        {
            var context = factory.Create(httpContext, channel);

            Assert.Equal("/host/workable/realtime", context.Url);
            Assert.DoesNotContain("secret-bearer-token", context.Url!, StringComparison.Ordinal);
            Assert.DoesNotContain("connection-id", context.Url!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KeepAnExplicitContextAuthoritativeWhileAnotherSnapshotIsActive()
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
        };
        var explicitActor = new WorkActor("explicit-user");
        var actors = new RecordingActorFactory(explicitActor);
        var factory = new HttpContextWorkRequestContextFactory(actors);
        var explicitContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, explicitActor.Id!)],
                "Explicit")),
        };

        using var scope = WorkableAspNetCoreAuthentication.UseSnapshot(activeSnapshot);
        var context = factory.Create(explicitContext, WorkInvocationChannel.HttpApi);

        Assert.Same(explicitContext, actors.LastHttpContext);
        Assert.Equal(explicitActor, context.Actor);
        Assert.True(context.IsAuthenticated);
    }

    private sealed class RecordingActorFactory(WorkActor actor) : IWorkActorFactory
    {
        public HttpContext? LastHttpContext { get; private set; }

        public WorkActor Create(HttpContext? httpContext)
        {
            this.LastHttpContext = httpContext;
            return actor;
        }

        public WorkActor Create(ClaimsPrincipal? user)
            => actor;
    }
}

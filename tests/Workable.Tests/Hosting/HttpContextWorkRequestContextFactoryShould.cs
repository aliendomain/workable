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
        Assert.Equal("Queue through HTTP.", context.Description);
        Assert.Equal("/workable/custom/queue?definition=demo", context.Url);
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

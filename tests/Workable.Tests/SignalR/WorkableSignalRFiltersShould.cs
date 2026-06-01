using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableSignalRFiltersShould
{
    [Fact]
    public async Task RejectUnauthenticatedConnectionsBeforeContinuationRuns()
    {
        var filter = new WorkableSignalRAuthenticationFilter();
        var lifetime = new HubLifetimeContext(
            CallerContext(authenticated: false),
            Services(),
            new TestHub());
        var continued = false;

        var exception = await Assert.ThrowsAsync<HubException>(() => filter.OnConnectedAsync(lifetime, _ =>
        {
            continued = true;
            return Task.CompletedTask;
        }));

        Assert.Equal("Authentication is required.", exception.Message);
        Assert.False(continued);
    }

    [Fact]
    public async Task InvokeAuthenticatedMethodsAndReturnResult()
    {
        var filter = new WorkableSignalRAuthenticationFilter();
        var invocation = InvocationContext(authenticated: true);
        var continued = false;

        var result = await filter.InvokeMethodAsync(invocation, _ =>
        {
            continued = true;
            return ValueTask.FromResult<object?>("accepted");
        });

        Assert.True(continued);
        Assert.Equal("accepted", result);
    }

    [Fact]
    public async Task RejectUnauthenticatedMethodsBeforeContinuationRuns()
    {
        var filter = new WorkableSignalRAuthenticationFilter();
        var invocation = InvocationContext(authenticated: false);
        var continued = false;

        var exception = await Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(invocation, _ =>
        {
            continued = true;
            return ValueTask.FromResult<object?>(null);
        }));

        Assert.Equal("Authentication is required.", exception.Message);
        Assert.False(continued);
    }

    [Fact]
    public async Task TranslateWorkSystemAccessDenialsToHubExceptions()
    {
        var filter = new WorkableSignalRAuthorizationFilter();
        var denied = new WorkSystemAccessDeniedException(
            WorkSystemPermission.ViewDiagnostics,
            WorkSystemId.New(),
            "secure");

        var exception = await Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(
            InvocationContext(authenticated: true),
            _ => throw denied));

        Assert.Equal(denied.Message, exception.Message);
    }

    [Fact]
    public async Task LeaveOtherMethodExceptionsUnchanged()
    {
        var filter = new WorkableSignalRAuthorizationFilter();
        var original = new InvalidOperationException("boom");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await filter.InvokeMethodAsync(
            InvocationContext(authenticated: true),
            _ => throw original));

        Assert.Same(original, exception);
    }

    private static HubInvocationContext InvocationContext(bool authenticated)
        => new(
            CallerContext(authenticated),
            Services(),
            new TestHub(),
            typeof(TestHub).GetMethod(nameof(TestHub.TestMethod), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("Expected test hub method."),
            []);

    private static TestHubCallerContext CallerContext(bool authenticated)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = Services(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                authenticated ? [new Claim(ClaimTypes.NameIdentifier, "signalr-user")] : [],
                authenticated ? "Test" : null)),
        };
        return new TestHubCallerContext(context);
    }

    private static IServiceProvider Services()
        => new ServiceCollection().BuildServiceProvider();

    private sealed class TestHub : Hub
    {
        public Task TestMethod()
            => Task.CompletedTask;
    }

    private sealed class TestHubCallerContext(HttpContext httpContext) : HubCallerContext
    {
        private readonly CancellationTokenSource connectionAborted = new();
        private readonly FeatureCollection features = CreateFeatures(httpContext);

        public override string ConnectionId { get; } = "connection-1";

        public override string? UserIdentifier => "signalr-user";

        public override ClaimsPrincipal? User => httpContext.User;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features => this.features;

        public override CancellationToken ConnectionAborted => this.connectionAborted.Token;

        public override void Abort()
            => this.connectionAborted.Cancel();

        private static FeatureCollection CreateFeatures(HttpContext httpContext)
        {
            var features = new FeatureCollection();
            features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));
            return features;
        }
    }

    private sealed class TestHttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}

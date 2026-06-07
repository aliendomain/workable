using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class HttpContextWorkCommandDispatcherShould
{
    [Fact]
    public async Task UseCurrentHttpContextToCreateRequestContext()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder => builder.AddWork<HttpDispatchEchoWork>(
                WorkDefinition.Create("http.dispatch.echo")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkCommandDispatcher>();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = CreateHttpContext();

        var result = await dispatcher.Dispatch<HttpDispatchInput, HttpDispatchOutput>(
            "http.dispatch.echo",
            new HttpDispatchInput("alpha"),
            "Dispatch through HTTP.");

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkDispatchStatus.Completed, result.Status);
        var response = Assert.IsType<HttpDispatchOutput>(result.Response);
        Assert.Equal("alpha", response.Input);
        Assert.Equal(WorkInvocationChannel.HttpApi, response.Channel);
        Assert.Equal("http-user", response.ActorId);
        Assert.Equal("Http User", response.ActorName);
        Assert.Equal("http-user@example.test", response.ActorEmail);
        Assert.Equal("Dispatch through HTTP.", response.Description);
        Assert.Equal("/workable/commands/dispatch?mode=test", response.Url);
        Assert.True(response.IsAuthenticated);
    }

    [Fact]
    public async Task ReturnRequestContextUnavailableWhenHttpContextIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder => builder.AddWork<HttpDispatchEchoWork>(
                WorkDefinition.Create("http.dispatch.echo")))
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkCommandDispatcher>();

        var result = await dispatcher.Dispatch<HttpDispatchInput, HttpDispatchOutput>(
            "http.dispatch.echo",
            new HttpDispatchInput("beta"),
            "Dispatch through HTTP.");

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkDispatchStatus.RequestContextUnavailable, result.Status);
        Assert.Equal("workable.dispatch.http_context.unavailable", result.ErrorCode);
        Assert.Equal(
            "The command could not be completed because no current HTTP request context was available.",
            result.ErrorMessage);
        Assert.Null(result.QueueOutcome);
        Assert.Null(result.Completion);
    }

    private static HttpContext CreateHttpContext()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "http-user"),
                    new Claim(ClaimTypes.Name, "Http User"),
                    new Claim(ClaimTypes.Email, "http-user@example.test"),
                ],
                authenticationType: "Test")),
        };
        httpContext.Request.PathBase = "/workable";
        httpContext.Request.Path = "/commands/dispatch";
        httpContext.Request.QueryString = new QueryString("?mode=test");
        return httpContext;
    }

    private sealed record HttpDispatchInput(string Value);

    private sealed record HttpDispatchOutput(
        string Input,
        WorkInvocationChannel Channel,
        string? ActorId,
        string? ActorName,
        string? ActorEmail,
        string? Description,
        string? Url,
        bool IsAuthenticated);

    private sealed class HttpDispatchEchoWork : IWorkExecutor<HttpDispatchInput, HttpDispatchOutput>
    {
        public Task<WorkExecutionResult<HttpDispatchOutput>> Execute(
            IWorkExecutionContext context,
            HttpDispatchInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult<HttpDispatchOutput>.Success(new HttpDispatchOutput(
                input.Value,
                context.RequestContext.Channel,
                context.RequestContext.Actor.Id,
                context.RequestContext.Actor.Name,
                context.RequestContext.Actor.Email,
                context.RequestContext.Description,
                context.RequestContext.Url,
                context.RequestContext.IsAuthenticated)));
    }
}

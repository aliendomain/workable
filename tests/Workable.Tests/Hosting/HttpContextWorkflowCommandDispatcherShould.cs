using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class HttpContextWorkflowCommandDispatcherShould
{
    [Fact]
    public async Task UseCurrentHttpContextToCreateRequestContext()
    {
        WorkflowHttpCommandCapture? captured = null;
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("http.workflow.command.child"),
                    (context, _, _) =>
                    {
                        captured = WorkflowHttpCommandCapture.From(context.RequestContext);
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.command.start"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.command.child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = CreateHttpContext();

        var result = await dispatcher.Start(
            "http.workflow.command.start",
            "Start workflow through HTTP.");

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Completed, result.Status);
        Assert.NotNull(result.RunId);
        Assert.Equal(WorkflowRunStatus.Completed, result.RunStatus);
        Assert.NotNull(captured);
        Assert.Equal(WorkInvocationChannel.HttpApi, captured.Channel);
        Assert.Equal(WorkOriginSurface.HostApplication, captured.Surface);
        Assert.Equal("http-workflow-user", captured.ActorId);
        Assert.Equal("Http Workflow User", captured.ActorName);
        Assert.Equal("http-workflow-user@example.test", captured.ActorEmail);
        Assert.Equal("Start workflow through HTTP.", captured.Description);
        Assert.Equal("/workable/workflows/start", captured.Url);
        Assert.True(captured.IsAuthenticated);
    }

    [Fact]
    public async Task AuthenticateTheExplicitWorkableSchemeForACustomWorkflowEndpoint()
    {
        WorkflowHttpCommandCapture? captured = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSchemeTestAuthentication();
        services.AddWorkableAspNetCoreAuthorization();
        services.AddWorkableSystem(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("http.workflow.explicit.child"),
                (context, _, _) =>
                {
                    captured = WorkflowHttpCommandCapture.From(context.RequestContext);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.explicit"),
                workflow => workflow.DispatchWork(
                    "dispatch",
                    WorkDefinition.Create("http.workflow.explicit.child")));
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        await provider.GetRequiredService<IWorkSystemRegistry>().Default.Start();
        await using var scope = provider.CreateAsyncScope();
        var context = CreateHttpContext();
        context.RequestServices = scope.ServiceProvider;
        context.Request.Headers.Authorization =
            WorkableSchemeAuthenticationTestSupport.CreateBearerHeader().ToString();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var result = await scope.ServiceProvider
            .GetRequiredService<IHttpContextWorkflowCommandDispatcher>()
            .Start("http.workflow.explicit");

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal("workable-user-1", captured.ActorId);
        Assert.True(captured.IsAuthenticated);
        Assert.Equal("http-workflow-user", context.User.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task StartWorkflowInNamedSystem()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem("http-workflow-system", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("http.workflow.command.named.child"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.command.named"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.command.named.child")));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("http-workflow-system", out var system));
        await system.Start();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = CreateHttpContext();

        var result = await dispatcher.StartInSystem(
            "http-workflow-system",
            "http.workflow.command.named",
            "Start named workflow through HTTP.");

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Completed, result.Status);
        Assert.NotNull(result.RunId);
        Assert.Equal(WorkflowRunStatus.Completed, result.RunStatus);
    }

    [Fact]
    public async Task StartWorkflowWithInput()
    {
        WorkflowHttpCommandCapture? capturedContext = null;
        WorkInput? capturedInput = null;
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("http.workflow.command.input.child"),
                    (context, input, _) =>
                    {
                        capturedContext = WorkflowHttpCommandCapture.From(context.RequestContext);
                        capturedInput = input;
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.command.input"),
                    workflow => workflow.DispatchWorkFromWorkflowInput(
                        "dispatch",
                        WorkDefinition.Create("http.workflow.command.input.child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = CreateHttpContext();

        var result = await dispatcher.Start(
            "http.workflow.command.input",
            WorkInput.FromValue(new WorkflowHttpCommandInput("http-context-42")),
            "Start workflow with input through HTTP.");

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedContext);
        Assert.Equal(WorkInvocationChannel.HttpApi, capturedContext!.Channel);
        Assert.Equal("Start workflow with input through HTTP.", capturedContext.Description);
        Assert.NotNull(capturedInput);
        var payload = capturedInput!.ToValue<WorkflowHttpCommandInput>()
            ?? throw new InvalidOperationException("Expected workflow input payload.");
        Assert.Equal("http-context-42", payload.Value);
    }

    [Fact]
    public async Task ExecuteWorkflowAction()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("http.workflow.command.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.command.pause"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("http.workflow.command.slow"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = CreateHttpContext();
        var start = await dispatcher.Start(
            "http.workflow.command.pause",
            "Start workflow through HTTP.",
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        await started.Task.WaitAsync(CancellationToken.None);

        try
        {
            var pause = await dispatcher.Execute(
                start.RunId!.Value,
                WorkflowRunAction.Pause,
                "Pause workflow through HTTP.");

            Assert.True(pause.IsSuccess);
            Assert.Equal(WorkflowCommandStatus.Accepted, pause.Status);
            Assert.Equal(start.RunId, pause.RunId);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecuteWorkflowActionInNamedSystem()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem("http-workflow-system", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("http.workflow.command.named.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.command.named.pause"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("http.workflow.command.named.slow"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("http-workflow-system", out var system));
        await system.Start();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = CreateHttpContext();
        var start = await dispatcher.StartInSystem(
            "http-workflow-system",
            "http.workflow.command.named.pause",
            "Start named workflow through HTTP.",
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        await started.Task.WaitAsync(CancellationToken.None);

        try
        {
            var pause = await dispatcher.ExecuteInSystem(
                "http-workflow-system",
                start.RunId!.Value,
                WorkflowRunAction.Pause,
                "Pause named workflow through HTTP.");

            Assert.True(pause.IsSuccess);
            Assert.Equal(WorkflowCommandStatus.Accepted, pause.Status);
            Assert.Equal(start.RunId, pause.RunId);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ReturnRequestContextUnavailableWhenHttpContextIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.command.start"),
                workflow => workflow.Join("complete")))
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "http.workflow.command.start",
            "Start workflow through HTTP.");

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.RequestContextUnavailable, result.Status);
        Assert.Equal("workable.workflow.dispatch.http_context.unavailable", result.ErrorCode);
        Assert.Equal(
            "The workflow command could not be completed because no current HTTP request context was available.",
            result.ErrorMessage);
    }

    [Fact]
    public async Task ReturnRequestContextUnavailableWhenExecutingWithoutHttpContext()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableAspNetCoreAuthorization()
            .AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.command.start"),
                workflow => workflow.Join("complete")))
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IHttpContextWorkflowCommandDispatcher>();

        var result = await dispatcher.Execute(
            WorkflowRunId.New(),
            WorkflowRunAction.Cancel,
            "Cancel workflow through HTTP.");

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.RequestContextUnavailable, result.Status);
        Assert.Equal("workable.workflow.dispatch.http_context.unavailable", result.ErrorCode);
        Assert.Equal(
            "The workflow command could not be completed because no current HTTP request context was available.",
            result.ErrorMessage);
    }

    private static HttpContext CreateHttpContext()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "http-workflow-user"),
                    new Claim(ClaimTypes.Name, "Http Workflow User"),
                    new Claim(ClaimTypes.Email, "http-workflow-user@example.test"),
                ],
                authenticationType: "Test")),
        };
        httpContext.Request.PathBase = "/workable";
        httpContext.Request.Path = "/workflows/start";
        httpContext.Request.QueryString = new QueryString("?mode=test");
        return httpContext;
    }

    private sealed record WorkflowHttpCommandCapture(
        WorkInvocationChannel Channel,
        WorkOriginSurface Surface,
        string? ActorId,
        string? ActorName,
        string? ActorEmail,
        string? Description,
        string? Url,
        bool IsAuthenticated)
    {
        public static WorkflowHttpCommandCapture From(WorkRequestContext context)
            => new(
                context.Channel,
                context.Surface,
                context.Actor.Id,
                context.Actor.Name,
                context.Actor.Email,
                context.Description,
                context.Url,
                context.IsAuthenticated);
    }

    private sealed record WorkflowHttpCommandInput(string Value);
}

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class WorkflowCommandDispatcherShould
{
    [Fact]
    public async Task StartWorkflowAndPreserveRequestContext()
    {
        WorkflowCommandCapture? captured = null;
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.child"),
                    (context, _, _) =>
                    {
                        captured = WorkflowCommandCapture.From(context.RequestContext);
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.start"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.command.child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("workflow-user", "Workflow User"),
            "Start workflow through a test command.",
            "https://workable.test/workflows/start",
            isAuthenticated: true);

        var result = await dispatcher.Start("workflow.command.start", requestContext);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Completed, result.Status);
        Assert.NotNull(result.RunId);
        Assert.Equal(WorkflowRunStatus.Completed, result.RunStatus);
        Assert.NotNull(captured);
        Assert.Equal(WorkInvocationChannel.HttpApi, captured.Channel);
        Assert.Equal("workflow-user", captured.ActorId);
        Assert.Equal("Workflow User", captured.ActorName);
        Assert.Equal("Start workflow through a test command.", captured.Description);
        Assert.Equal("https://workable.test/workflows/start", captured.Url);
        Assert.True(captured.IsAuthenticated);
    }

    [Fact]
    public async Task StartWorkflowWithInput()
    {
        WorkInput? captured = null;
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.input.child"),
                    (_, input, _) =>
                    {
                        captured = input;
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.input"),
                    workflow => workflow.DispatchWorkFromWorkflowInput(
                        "dispatch",
                        WorkDefinition.Create("workflow.command.input.child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.input",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkInput.FromValue(new WorkflowCommandInput("command-42")));

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        var payload = Assert.IsType<WorkflowCommandInput>(captured!.ToValue<WorkflowCommandInput>());
        Assert.Equal("command-42", payload.Value);
    }

    [Fact]
    public async Task StartWorkflowInNamedSystem()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem("workflow-system", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.named.child"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.named"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.command.named.child")));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("workflow-system", out var system));
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow-system",
            "workflow.command.named",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Completed, result.Status);
        Assert.NotNull(result.RunId);
        Assert.Equal(WorkflowRunStatus.Completed, result.RunStatus);
    }

    [Fact]
    public async Task ReturnAcceptedResultWhenConfiguredNotToWait()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.wait"),
                    async (_, _, cancellationToken) =>
                    {
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.no-wait"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.command.wait")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.no-wait",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        release.TrySetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Accepted, result.Status);
        Assert.NotNull(result.RunId);
        Assert.Equal(WorkflowRunStatus.Running, result.RunStatus);
    }

    [Fact]
    public async Task AcceptedWorkflowCanReachBlockedStateWhenChildFails()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.fail"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Failure([
                        WorkMessage.Error("workflow.command.child.failed", "Child failed."),
                    ])));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.blocked"),
                    workflow => workflow
                        .DispatchWork("fail", WorkDefinition.Create("workflow.command.fail"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.blocked",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        var runId = result.RunId ?? throw new InvalidOperationException("Expected workflow run id.");
        WorkflowRunSnapshot? blocked = null;
        await TestEventually.Until(
            () =>
            {
                blocked = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(runId);
                return blocked?.Status == WorkflowRunStatus.Blocked;
            },
            "Expected the workflow to block when a joined child failed.");

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Accepted, result.Status);
        Assert.Equal(WorkflowRunStatus.Running, result.RunStatus);
        Assert.Equal(WorkflowRunStatus.Blocked, blocked!.Status);
        Assert.Contains(blocked.Messages, message => message.Code == "workflow.command.child.failed");
    }

    [Fact]
    public async Task ReturnFailedWhenWorkflowDefinitionIsInvalidAtStart()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.missing-child"),
                    workflow => workflow.DispatchWork("missing", WorkDefinition.Create("workflow.command.no-child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.missing-child",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Failed, result.Status);
        Assert.NotNull(result.RunId);
        Assert.Equal(WorkflowRunStatus.Failed, result.RunStatus);
        Assert.Equal("workable.definition.not_found", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnInvalidWhenDurableWorkflowCannotStart()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.durable.child"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        "workflow.command.durable",
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.command.durable.child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.durable",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Invalid, result.Status);
        Assert.Null(result.RunId);
        Assert.Null(result.RunStatus);
        Assert.Equal("workable.workflow.coordination.persistence_store_required", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnUnauthorizedWhenCallerCannotOperateWorkflow()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.secured.child"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.secured"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.command.secured.child")),
                    authorize: auth => auth.AllowOperateToGroups("workflow.ops"));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.secured",
            WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                new WorkActor("workflow-user", "Workflow User"),
                isAuthenticated: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Unauthorized, result.Status);
        Assert.Null(result.RunId);
        Assert.Null(result.RunStatus);
        Assert.Equal("workable.workflow.definition.unauthorized", result.ErrorCode);
    }

    [Fact]
    public async Task ExecutePauseWorkflowAction()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.pause"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("workflow.command.slow"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();
        var start = await dispatcher.Start(
            "workflow.command.pause",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        var runId = start.RunId ?? throw new InvalidOperationException("Expected workflow run id.");
        await started.Task.WaitAsync(CancellationToken.None);

        try
        {
            var pause = await dispatcher.Execute(
                runId,
                WorkflowRunAction.Pause,
                WorkRequestContext.Create(WorkInvocationChannel.InProcess));

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
    public async Task ExecuteCancelWorkflowActionAndMapCanceledCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.cancel.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.cancel"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("workflow.command.cancel.slow"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();
        var start = await dispatcher.Start(
            "workflow.command.cancel",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        var runId = start.RunId ?? throw new InvalidOperationException("Expected workflow run id.");
        await started.Task.WaitAsync(CancellationToken.None);

        try
        {
            var cancel = await dispatcher.Execute(
                runId,
                WorkflowRunAction.Cancel,
                WorkRequestContext.Create(WorkInvocationChannel.InProcess));
            release.TrySetResult();
            WorkflowRunSnapshot? canceled = null;
            await TestEventually.Until(
                () =>
                {
                    canceled = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(runId);
                    return canceled?.Status == WorkflowRunStatus.Canceled;
                },
                "Expected the workflow to cancel.");

            Assert.True(cancel.IsSuccess);
            Assert.Equal(WorkflowCommandStatus.Accepted, cancel.Status);
            Assert.Equal(start.RunId, cancel.RunId);
            Assert.Equal(WorkflowRunStatus.Running, cancel.RunStatus);
            Assert.Equal(WorkflowRunStatus.Canceled, canceled!.Status);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecuteStartWorkflowActionAgainstPausedRun()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.resume.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.resume"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("workflow.command.resume.slow"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();
        var start = await dispatcher.Start(
            "workflow.command.resume",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted));
        var runId = start.RunId ?? throw new InvalidOperationException("Expected workflow run id.");
        await started.Task.WaitAsync(CancellationToken.None);
        var pause = await dispatcher.Execute(
            runId,
            WorkflowRunAction.Pause,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        Assert.True(pause.IsSuccess);
        await TestEventually.Until(
            () => ((InMemoryWorkSystem)system).WorkflowRuntime.Get(runId)?.Status == WorkflowRunStatus.Paused,
            "Expected the workflow to pause.");

        try
        {
            var resume = await dispatcher.Execute(
                runId,
                WorkflowRunAction.Start,
                WorkRequestContext.Create(WorkInvocationChannel.InProcess));

            Assert.True(resume.IsSuccess);
            Assert.Equal(WorkflowCommandStatus.Accepted, resume.Status);
            Assert.Equal(start.RunId, resume.RunId);
            Assert.Equal(WorkflowRunStatus.Running, resume.RunStatus);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ReturnActionNotFoundWhenRunIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.command.start"),
                workflow => workflow.Join("complete")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();
        var missingRunId = WorkflowRunId.New();

        var result = await dispatcher.Execute(
            missingRunId,
            WorkflowRunAction.Cancel,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.NotFound, result.Status);
        Assert.Equal(missingRunId, result.RunId);
        Assert.Null(result.RunStatus);
        Assert.Equal("workable.workflow.run.not_found", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnActionInvalidWhenRunIsFinal()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("workflow.command.final.child"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.command.final"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.command.final.child")));
            })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();
        var start = await dispatcher.Start(
            "workflow.command.final",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var runId = start.RunId ?? throw new InvalidOperationException("Expected workflow run id.");

        var result = await dispatcher.Execute(
            runId,
            WorkflowRunAction.Cancel,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.Invalid, result.Status);
        Assert.Equal(start.RunId, result.RunId);
        Assert.Equal(WorkflowRunStatus.Completed, result.RunStatus);
        Assert.Equal("workable.workflow.run.final", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnActionSystemNotFoundWhenNamedSystemIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.command.start"),
                workflow => workflow.Join("complete")))
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Execute(
            "missing",
            WorkflowRunId.New(),
            WorkflowRunAction.Cancel,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.SystemNotFound, result.Status);
        Assert.Null(result.RunId);
        Assert.Null(result.RunStatus);
        Assert.Equal("workable.workflow.dispatch.system.not_found", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnSystemNotFoundWhenNamedSystemIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.command.start"),
                workflow => workflow.Join("complete")))
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "missing",
            "workflow.command.start",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.SystemNotFound, result.Status);
        Assert.Null(result.RunId);
        Assert.Null(result.RunStatus);
        Assert.Equal("workable.workflow.dispatch.system.not_found", result.ErrorCode);
        Assert.Equal("The 'missing' Workable system is not registered.", result.ErrorMessage);
    }

    [Fact]
    public async Task ReturnNotFoundWhenWorkflowDefinitionIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.command.start"),
                workflow => workflow.Join("complete")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkflowCommandDispatcher>();

        var result = await dispatcher.Start(
            "workflow.command.missing",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowCommandStatus.NotFound, result.Status);
        Assert.Null(result.RunId);
        Assert.Null(result.RunStatus);
        Assert.Equal("workable.workflow.definition.not_found", result.ErrorCode);
        Assert.Equal("Workflow 'workflow.command.missing' was not found.", result.ErrorMessage);
    }

    [Theory]
    [InlineData((int)WorkflowStartStatus.Accepted, WorkflowCommandStatus.Accepted)]
    [InlineData((int)WorkflowStartStatus.Invalid, WorkflowCommandStatus.Invalid)]
    [InlineData((int)WorkflowStartStatus.Unauthorized, WorkflowCommandStatus.Unauthorized)]
    [InlineData((int)WorkflowStartStatus.NotFound, WorkflowCommandStatus.NotFound)]
    [InlineData(999, WorkflowCommandStatus.Invalid)]
    public void MapWorkflowStartStatuses(int sourceValue, WorkflowCommandStatus expected)
    {
        var actual = InvokePrivate<WorkflowCommandStatus>(
            "ToCommandStatus",
            [typeof(WorkflowStartStatus)],
            (WorkflowStartStatus)sourceValue);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Running, WorkflowCommandStatus.Running)]
    [InlineData(WorkflowRunStatus.Paused, WorkflowCommandStatus.Paused)]
    [InlineData(WorkflowRunStatus.Blocked, WorkflowCommandStatus.Blocked)]
    [InlineData(WorkflowRunStatus.Completed, WorkflowCommandStatus.Completed)]
    [InlineData(WorkflowRunStatus.Failed, WorkflowCommandStatus.Failed)]
    [InlineData(WorkflowRunStatus.Canceled, WorkflowCommandStatus.Canceled)]
    [InlineData(WorkflowRunStatus.Invalid, WorkflowCommandStatus.Invalid)]
    [InlineData(WorkflowRunStatus.NotFound, WorkflowCommandStatus.NotFound)]
    [InlineData(WorkflowRunStatus.Unauthorized, WorkflowCommandStatus.Unauthorized)]
    [InlineData((WorkflowRunStatus)999, WorkflowCommandStatus.Invalid)]
    public void MapWorkflowRunStatuses(WorkflowRunStatus source, WorkflowCommandStatus expected)
    {
        var actual = InvokePrivate<WorkflowCommandStatus>(
            "ToCommandStatus",
            [typeof(WorkflowRunStatus)],
            source);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData((int)WorkflowActionStatus.Accepted, WorkflowCommandStatus.Accepted)]
    [InlineData((int)WorkflowActionStatus.NotFound, WorkflowCommandStatus.NotFound)]
    [InlineData((int)WorkflowActionStatus.Unauthorized, WorkflowCommandStatus.Unauthorized)]
    [InlineData((int)WorkflowActionStatus.Invalid, WorkflowCommandStatus.Invalid)]
    [InlineData(999, WorkflowCommandStatus.Invalid)]
    public void MapWorkflowActionStatuses(int sourceValue, WorkflowCommandStatus expected)
    {
        var actual = InvokePrivate<WorkflowCommandStatus>(
            "ToCommandStatus",
            [typeof(WorkflowActionStatus)],
            (WorkflowActionStatus)sourceValue);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(WorkflowRunAction.Start, (int)WorkflowAction.Start)]
    [InlineData(WorkflowRunAction.Pause, (int)WorkflowAction.Pause)]
    [InlineData(WorkflowRunAction.Cancel, (int)WorkflowAction.Cancel)]
    [InlineData((WorkflowRunAction)999, (int)WorkflowAction.Cancel)]
    public void MapWorkflowRunActions(WorkflowRunAction source, int expectedValue)
    {
        var actual = InvokePrivate<WorkflowAction>(
            "ToWorkflowAction",
            [typeof(WorkflowRunAction)],
            source);

        Assert.Equal((WorkflowAction)expectedValue, actual);
    }

    private static T InvokePrivate<T>(
        string name,
        Type[] parameterTypes,
        object argument)
    {
        var method = typeof(WorkflowCommandDispatcher).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            parameterTypes,
            modifiers: null)
            ?? throw new InvalidOperationException($"Expected private method '{name}'.");

        return (T)(method.Invoke(null, [argument])
            ?? throw new InvalidOperationException($"Private method '{name}' returned null."));
    }

    private sealed record WorkflowCommandCapture(
        WorkInvocationChannel Channel,
        string? ActorId,
        string? ActorName,
        string? Description,
        string? Url,
        bool IsAuthenticated)
    {
        public static WorkflowCommandCapture From(WorkRequestContext context)
            => new(
                context.Channel,
                context.Actor.Id,
                context.Actor.Name,
                context.Description,
                context.Url,
                context.IsAuthenticated);
    }

    private sealed record WorkflowCommandInput(string Value);
}

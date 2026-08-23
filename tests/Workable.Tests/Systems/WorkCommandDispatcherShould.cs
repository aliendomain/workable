using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class WorkCommandDispatcherShould
{
    [Fact]
    public void MapEveryCompletionStatusToItsDispatchStatus()
    {
        var method = typeof(WorkCommandDispatcher).GetMethod(
            "ToDispatchStatus",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkCompletionStatus)],
            modifiers: null);
        Assert.NotNull(method);

        foreach (var status in Enum.GetValues<WorkCompletionStatus>())
        {
            var expected = status switch
            {
                WorkCompletionStatus.Executing => WorkDispatchStatus.Executing,
                WorkCompletionStatus.Completed => WorkDispatchStatus.Completed,
                WorkCompletionStatus.Failed => WorkDispatchStatus.Failed,
                WorkCompletionStatus.Paused => WorkDispatchStatus.Paused,
                WorkCompletionStatus.Interrupted => WorkDispatchStatus.Interrupted,
                WorkCompletionStatus.Canceled => WorkDispatchStatus.Canceled,
                WorkCompletionStatus.NotFound => WorkDispatchStatus.NotFound,
                _ => WorkDispatchStatus.Invalid,
            };
            Assert.Equal(expected, method.Invoke(null, [status]));
        }

        Assert.Equal(WorkDispatchStatus.Invalid, method.Invoke(null, [(WorkCompletionStatus)int.MaxValue]));
    }

    [Fact]
    public void MapEveryQueueStatusAndPreserveBlankErrorCodesWithoutInventingMessages()
    {
        var map = typeof(WorkCommandDispatcher).GetMethod(
            "ToDispatchStatus",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkQueueStatus)],
            modifiers: null)!;
        foreach (var status in Enum.GetValues<WorkQueueStatus>())
        {
            var expected = status switch
            {
                WorkQueueStatus.Accepted => WorkDispatchStatus.Accepted,
                WorkQueueStatus.Invalid => WorkDispatchStatus.Invalid,
                WorkQueueStatus.Unauthorized => WorkDispatchStatus.Unauthorized,
                WorkQueueStatus.NotFound => WorkDispatchStatus.NotFound,
                _ => WorkDispatchStatus.Invalid,
            };
            Assert.Equal(expected, map.Invoke(null, [status]));
        }

        Assert.Equal(WorkDispatchStatus.Invalid, map.Invoke(null, [(WorkQueueStatus)int.MaxValue]));

        var createNotFound = typeof(WorkCommandDispatcher).GetMethod(
            "CreateSystemNotFoundResult",
            BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(string));
        var missingDefault = Assert.IsType<WorkDispatchResult<string>>(createNotFound.Invoke(null, [null]));
        Assert.Equal("The default Workable system is not registered.", missingDefault.ErrorMessage);

        var createResult = typeof(WorkCommandDispatcher).GetMethod(
            "CreateResult",
            BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(string));
        var blankError = WorkMessage.Error("blank.error", " ");
        var result = Assert.IsType<WorkDispatchResult<string>>(createResult.Invoke(
            null,
            [WorkDispatchStatus.Invalid, null, null, new[] { blankError }, null, null]));
        Assert.Equal("blank.error", result.ErrorCode);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task DispatchTypedWorkAndPreserveRequestContext()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<DispatchEchoWork>(
                WorkDefinition.Create("dispatch.echo")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkCommandDispatcher>();
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("dispatcher-user", "Dispatcher User"),
            "Dispatch through a test command.",
            "https://workable.test/dispatch",
            isAuthenticated: true);

        var result = await dispatcher.Dispatch<DispatchInput, DispatchOutput>(
            "dispatch.echo",
            new DispatchInput("alpha"),
            requestContext);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkDispatchStatus.Completed, result.Status);
        var response = Assert.IsType<DispatchOutput>(result.Response);
        Assert.Equal("alpha", response.Input);
        Assert.Equal(WorkInvocationChannel.HttpApi, response.Channel);
        Assert.Equal("dispatcher-user", response.ActorId);
        Assert.Equal("Dispatcher User", response.ActorName);
        Assert.Equal("Dispatch through a test command.", response.Description);
        Assert.Equal("https://workable.test/dispatch", response.Url);
        Assert.True(response.IsAuthenticated);
        Assert.NotNull(result.WorkerId);
        Assert.Equal(WorkQueueStatus.Accepted, result.QueueOutcome?.Status);
        Assert.Equal(WorkCompletionStatus.Completed, result.Completion?.Status);
    }

    [Fact]
    public async Task ReturnAcceptedResultWhenConfiguredNotToWait()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<DispatchEchoWork>(
                WorkDefinition.Create("dispatch.echo")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkCommandDispatcher>();

        var result = await dispatcher.Dispatch<DispatchInput, DispatchOutput>(
            "dispatch.echo",
            new DispatchInput("beta"),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            new WorkDispatchOptions(WorkDispatchCompletion.ReturnAfterAccepted));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkDispatchStatus.Accepted, result.Status);
        Assert.Null(result.Response);
        Assert.NotNull(result.WorkerId);
        Assert.Equal(WorkQueueStatus.Accepted, result.QueueOutcome?.Status);
        Assert.Null(result.Completion);
    }

    [Fact]
    public async Task ReturnSystemNotFoundWhenNamedSystemIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<DispatchEchoWork>(
                WorkDefinition.Create("dispatch.echo")))
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IWorkCommandDispatcher>();

        var result = await dispatcher.Dispatch<DispatchInput, DispatchOutput>(
            "missing",
            "dispatch.echo",
            new DispatchInput("gamma"),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkDispatchStatus.SystemNotFound, result.Status);
        Assert.Null(result.WorkerId);
        Assert.Equal("workable.dispatch.system.not_found", result.ErrorCode);
        Assert.Equal("The 'missing' Workable system is not registered.", result.ErrorMessage);
        Assert.Null(result.QueueOutcome);
        Assert.Null(result.Completion);
    }

    [Fact]
    public async Task ReturnWorkNotFoundWhenDefinitionIsMissing()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<DispatchEchoWork>(
                WorkDefinition.Create("dispatch.echo")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dispatcher = provider.GetRequiredService<IWorkCommandDispatcher>();

        var result = await dispatcher.Dispatch<DispatchInput, DispatchOutput>(
            "dispatch.missing",
            new DispatchInput("delta"),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkDispatchStatus.NotFound, result.Status);
        Assert.Equal(WorkQueueStatus.NotFound, result.QueueOutcome?.Status);
        Assert.Equal("workable.definition.not_found", result.ErrorCode);
        Assert.Equal("No work definition was found for 'dispatch.missing'.", result.ErrorMessage);
        Assert.Null(result.Completion);
    }

    private sealed record DispatchInput(string Value);

    private sealed record DispatchOutput(
        string Input,
        WorkInvocationChannel Channel,
        string? ActorId,
        string? ActorName,
        string? Description,
        string? Url,
        bool IsAuthenticated);

    private sealed class DispatchEchoWork : IWorkExecutor<DispatchInput, DispatchOutput>
    {
        public Task<WorkExecutionResult<DispatchOutput>> Execute(
            IWorkExecutionContext context,
            DispatchInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult<DispatchOutput>.Success(new DispatchOutput(
                input.Value,
                context.RequestContext.Channel,
                context.RequestContext.Actor.Id,
                context.RequestContext.Actor.Name,
                context.RequestContext.Description,
                context.RequestContext.Url,
                context.RequestContext.IsAuthenticated)));
    }
}

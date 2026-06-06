using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Queueing")]
public sealed class WorkQueueServiceShould
{
    [Fact]
    public async Task RejectBlankNamedQueueRequestsBeforeLookup()
    {
        await using var system = CreateSystem(WorkDefinition.Create("queue.blank.name"));

        await Assert.ThrowsAsync<ArgumentException>(() => system.Queue.Enqueue(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => system.Queue.Enqueue(" ", new QueueInput("typed")));
    }

    [Fact]
    public async Task ReturnInvalidOutcomeWhenSessionChannelIsNotAllowed()
    {
        var definition = WorkDefinition.Create(
            "queue.dotnet.only",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            });
        await using var system = CreateSystem(definition);
        await system.Start();
        var session = system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            description: "Queue through HTTP."));

        var handle = await session.Queue.Enqueue(definition.Name);
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Null(handle.QueueOutcome.WorkerId);
        var message = Assert.Single(handle.QueueOutcome.Messages);
        Assert.Equal("workable.invocation.channel_not_allowed", message.Code);
        Assert.Equal("invocation.channel", message.Target);
        Assert.Contains("HTTP API", message.Text);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Equal(handle.QueueOutcome.Messages, completion.Messages);
    }

    private static IWorkSystem CreateSystem(WorkDefinition definition)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed record QueueInput(string Value);
}

using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "RunOnceExecution")]
public sealed class RunOnceWorkerExecutionStrategyTests
{
    [Fact]
    public async Task ExecutorExceptionFailsWorkerAndPublishesFailedEvent()
    {
        var definition = WorkDefinition.Create("throws", "Throws during execution.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, ThrowingWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.failed"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("throws");
        var completion = await handle.WaitForCompletion();
        var workEvent = await ReadNext(reader);

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(WorkerState.Failed, completion.Worker?.State);
        Assert.Null(completion.Output);
        Assert.Contains(completion.Messages, message =>
            message.Code == "workable.execution.exception" &&
            message.Severity == WorkMessageSeverity.Error &&
            message.Text == "Boom.");
        Assert.Equal(handle.WorkerId, workEvent.WorkerId);
        Assert.Equal(definition.Id, workEvent.DefinitionId);
        Assert.Equal("worker.failed", workEvent.EventType);
    }

    private static Task<WorkExecutionResult> ThrowingWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Boom.");

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }
}

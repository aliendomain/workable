using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "RunOnceExecution")]
[Trait("Category", "Execution")]
public sealed class RunOnceWorkerExecutionStrategyTests
{
    [Fact]
    public async Task SynchronouslyCompletedExecutorCompletesWorker()
    {
        var definition = WorkDefinition.Create("sync-success", "Completes synchronously.");
        var system = CreateSystem(definition, SynchronousSuccessWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("sync-success");
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Equal(WorkerState.Completed, completion.Worker?.State);
        Assert.Null(completion.Output);
    }

    [Fact]
    public async Task CanceledExecutorTaskCompletesWorkerAsCanceled()
    {
        var definition = WorkDefinition.Create("task-canceled", "Returns a canceled execution task.");
        var system = CreateSystem(definition, CanceledWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("task-canceled");
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.Equal(WorkerState.Canceled, completion.Worker?.State);
    }

    [Fact]
    public async Task ExecutorExceptionFailsWorkerAndPublishesFailedEvent()
    {
        var definition = WorkDefinition.Create("throws", "Throws during execution.");
        var system = CreateSystem(definition, ThrowingWork);

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
        Assert.Equal(definition.Name, workEvent.WorkDefinitionName);
        Assert.Equal("worker.failed", workEvent.EventType);
    }

    private static Task<WorkExecutionResult> SynchronousSuccessWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static Task<WorkExecutionResult> CanceledWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromCanceled<WorkExecutionResult>(new CancellationToken(canceled: true));

    private static Task<WorkExecutionResult> ThrowingWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Boom.");

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> executor)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, executor))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }
}

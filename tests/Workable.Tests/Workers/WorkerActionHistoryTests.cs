using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workers")]
public sealed class WorkerActionHistoryTests
{
    [Fact]
    public async Task DirectDotNetWorkerActionsRecordDurableHistory()
    {
        var definition = WorkDefinition.Create(
            "history.action",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("history.action");
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.cancel"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var outcome = await system.Workers.Execute(worker.Version, WorkAction.Cancel);
        var actionEvent = await ReadNext(reader);
        var updated = await system.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");
        var history = Assert.Single(updated.ActionHistory);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkerActionHistoryKind.WorkerAction, history.Kind);
        Assert.Equal(WorkAction.Cancel, history.Action);
        Assert.Equal(WorkActionStatus.Accepted, history.Status);
        Assert.Equal(WorkInvocationChannel.DotNet, history.Origin.Channel);
        Assert.Equal("Apply worker action 'Cancel' through .NET.", history.Origin.Description);
        Assert.Equal(worker.DefinitionName, actionEvent.WorkDefinitionName);
    }

    [Fact]
    public async Task ReconfigurationRecordsDurableHistoryWithRequestedChanges()
    {
        var definition = WorkDefinition.Create(
            "history.reconfigure",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("history.reconfigure");
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");

        var changes = new WorkerReconfiguration(ProfilingEnabled: true);
        var outcome = await system.Workers.Reconfigure(worker.Version, changes);
        var updated = await system.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");
        var history = Assert.Single(updated.ActionHistory);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkerActionHistoryKind.Reconfiguration, history.Kind);
        Assert.Null(history.Action);
        Assert.Equal(WorkActionStatus.Accepted, history.Status);
        Assert.Equal(WorkInvocationChannel.DotNet, history.Origin.Channel);
        Assert.Equal("Reconfigure worker through .NET.", history.Origin.Description);
        Assert.Same(changes, history.Reconfiguration);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }
}

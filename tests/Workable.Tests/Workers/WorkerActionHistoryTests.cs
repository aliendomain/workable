using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workers")]
public sealed class WorkerActionHistoryTests
{
    [Fact]
    public async Task CancellationRequestContextIsVisibleToExecutingCodeWithActionReason()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellationContext = new TaskCompletionSource<WorkRequestContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = WorkDefinition.Create("history.cancellation-context");
        await using var system = CreateSystem(definition, async (context, _, cancellationToken) =>
        {
            Assert.Null(context.CancellationRequestContext);
            executionStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorkExecutionResult.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                observedCancellationContext.TrySetResult(context.CancellationRequestContext);
                throw;
            }
        });
        await system.Start();

        var handle = await system.Queue.Enqueue(definition.Name);
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");
        var sessionRequestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("cancellation-user", "Cancellation User", "cancel@example.test"),
            description: "Operate the worker through the HTTP API.",
            url: "https://workable.test/workers/cancel",
            isAuthenticated: true);
        var session = await system.CreateSession(sessionRequestContext);

        var outcome = await session.Workers.Execute(
            worker.Version,
            new WorkerActionRequest(WorkAction.Cancel, "The customer withdrew the order."));
        var cancellationRequestContext = await observedCancellationContext.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        var canceledWorker = await system.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected canceled worker.");
        var history = Assert.Single(canceledWorker.ActionHistory);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.NotNull(cancellationRequestContext);
        Assert.Equal("cancellation-user", cancellationRequestContext.Actor.Id);
        Assert.Equal("Cancellation User", cancellationRequestContext.Actor.Name);
        Assert.Equal("cancel@example.test", cancellationRequestContext.Actor.Email);
        Assert.Equal("The customer withdrew the order.", cancellationRequestContext.Description);
        Assert.Equal("https://workable.test/workers/cancel", cancellationRequestContext.Url);
        Assert.Null(cancellationRequestContext.Authorization);
        Assert.Equal("The customer withdrew the order.", history.RequestContext.Description);
    }

    [Fact]
    public async Task DirectInProcessWorkerActionsRecordDurableHistory()
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
        Assert.Equal(WorkInvocationChannel.InProcess, history.Origin.Channel);
        Assert.Null(history.RequestContext.Description);
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
        Assert.Equal(WorkInvocationChannel.InProcess, history.Origin.Channel);
        Assert.Null(history.RequestContext.Description);
        Assert.Same(changes, history.Reconfiguration);
    }

    [Fact]
    public async Task ActionHistoryIsForgottenWhenAssociatedIterationFallsOutOfRetention()
    {
        var definition = WorkDefinition.Create(
            "history.retention",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMilliseconds(20)) with
                {
                    RetainedIterations = 1,
                },
            });
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("history.retention");
        var workerId = RequiredWorkerId(handle);

        await TestEventually.Until(async () =>
        {
            var current = await system.Query.Worker(workerId);
            return current is { State: WorkerState.Waiting, LastIterationSequence: >= 1 };
        });

        var worker = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");
        var changes = new WorkerReconfiguration(ProfilingEnabled: true);
        var outcome = await system.Workers.Reconfigure(worker.Version, changes);

        Assert.True(outcome.IsAccepted);

        await TestEventually.Until(async () =>
        {
            var current = await system.Query.Worker(workerId);
            return current is { LastIterationSequence: >= 2 } && current.ActionHistory.Count == 0;
        });
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

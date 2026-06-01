using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerActions")]
public sealed class WorkerBulkActionTests
{
    [Fact]
    public async Task ExecuteAllCancelsAllMatchingWorkers()
    {
        var system = CreateSystem(builder => builder
            .AddWork(ManualDefinition("bulk.cancel.one"), SuccessfulWork)
            .AddWork(ManualDefinition("bulk.cancel.two"), SuccessfulWork));
        await system.Start();
        var first = await system.Queue.Enqueue("bulk.cancel.one");
        var second = await system.Queue.Enqueue("bulk.cancel.two");

        var outcome = await system.Workers.ExecuteAll(WorkAction.Cancel);

        Assert.Equal(2, outcome.MatchedWorkerCount);
        Assert.Equal(2, outcome.AcceptedCount);
        Assert.Equal(WorkerState.Canceled, (await system.Query.Worker(first.WorkerId!.Value))?.State);
        Assert.Equal(WorkerState.Canceled, (await system.Query.Worker(second.WorkerId!.Value))?.State);
    }

    [Fact]
    public async Task ExecuteAllCanTargetCategoryIncludingSubcategories()
    {
        var system = CreateSystem(builder => builder
            .AddWork(ManualDefinition("bulk.category.invoice", category: "Billing:Invoices"), SuccessfulWork)
            .AddWork(ManualDefinition("bulk.category.email", category: "Email"), SuccessfulWork));
        await system.Start();
        var invoice = await system.Queue.Enqueue("bulk.category.invoice");
        var email = await system.Queue.Enqueue("bulk.category.email");

        var outcome = await system.Workers.ExecuteAll(
            WorkAction.Cancel,
            new WorkerBulkActionFilter(Category: "Billing"));

        Assert.Equal(1, outcome.MatchedWorkerCount);
        Assert.Equal(1, outcome.AcceptedCount);
        Assert.Equal(WorkerState.Canceled, (await system.Query.Worker(invoice.WorkerId!.Value))?.State);
        Assert.Equal(WorkerState.Queued, (await system.Query.Worker(email.WorkerId!.Value))?.State);
    }

    [Fact]
    public async Task ExecuteAllCanStartQueuedWorkers()
    {
        var system = CreateSystem(builder => builder
            .AddWork(ManualDefinition("bulk.start"), SuccessfulWork));
        await system.Start();
        var worker = await system.Queue.Enqueue("bulk.start");

        var outcome = await system.Workers.ExecuteAll(WorkAction.Start);
        var completion = await worker.WaitForCompletion();

        Assert.Equal(1, outcome.AcceptedCount);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
    }

    [Fact]
    public async Task ExecuteAllCanPauseRunningWorkers()
    {
        var tracker = new BulkActionTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .AddWork<PauseAwareWork>(WorkDefinition.Create("bulk.pause")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();
        var worker = await system.Queue.Enqueue("bulk.pause");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = await system.Workers.ExecuteAll(WorkAction.Pause);
        var completion = await worker.WaitForCompletion();

        Assert.Equal(1, outcome.AcceptedCount);
        Assert.Equal(WorkCompletionStatus.Paused, completion.Status);
        Assert.Equal(WorkerState.Paused, completion.Worker?.State);
    }

    [Fact]
    public async Task ExecuteAllCanPurgeFinalWorkers()
    {
        var system = CreateSystem(builder => builder
            .AddWork(WorkDefinition.Create("bulk.purge"), SuccessfulWork));
        await system.Start();
        var worker = await system.Queue.Enqueue("bulk.purge");
        var completion = await worker.WaitForCompletion();
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);

        var outcome = await system.Workers.ExecuteAll(WorkAction.Purge);

        Assert.Equal(1, outcome.AcceptedCount);
        Assert.Null(await system.Query.Worker(worker.WorkerId!.Value));
    }

    [Fact]
    public async Task ExecuteAllCanPushWaitingRecurringWorkersByCategory()
    {
        var billingAttempts = 0;
        var emailAttempts = 0;
        var system = CreateSystem(builder => builder
            .AddWork(
                RecurringDefinition("bulk.push.billing", "Billing:Invoices"),
                (context, input, cancellationToken) =>
                {
                    Interlocked.Increment(ref billingAttempts);
                    return Task.FromResult(WorkExecutionResult.Success());
                })
            .AddWork(
                RecurringDefinition("bulk.push.email", "Email"),
                (context, input, cancellationToken) =>
                {
                    Interlocked.Increment(ref emailAttempts);
                    return Task.FromResult(WorkExecutionResult.Success());
                }));
        await system.Start();
        var billing = await system.Queue.Enqueue("bulk.push.billing");
        var email = await system.Queue.Enqueue("bulk.push.email");
        var billingWorkerId = billing.WorkerId ?? throw new InvalidOperationException("Expected billing worker id.");
        var emailWorkerId = email.WorkerId ?? throw new InvalidOperationException("Expected email worker id.");

        try
        {
            await TestEventually.Until(async () =>
                Volatile.Read(ref billingAttempts) == 1 &&
                Volatile.Read(ref emailAttempts) == 1 &&
                await WorkerIsWaiting(system, billingWorkerId) &&
                await WorkerIsWaiting(system, emailWorkerId));

            var outcome = await system.Workers.ExecuteAll(
                WorkAction.Push,
                new WorkerBulkActionFilter(Category: "Billing"));

            Assert.Equal(1, outcome.MatchedWorkerCount);
            Assert.Equal(1, outcome.AcceptedCount);
            await TestEventually.Until(() => Volatile.Read(ref billingAttempts) >= 2);
            Assert.Equal(1, Volatile.Read(ref emailAttempts));
        }
        finally
        {
            await CancelIfActive(system, billingWorkerId);
            await CancelIfActive(system, emailWorkerId);
        }
    }

    [Fact]
    public async Task ExecuteAllReturnsValidationOutcomesForInvalidActions()
    {
        var system = CreateSystem(builder => builder
            .AddWork(ManualDefinition("bulk.push.invalid"), SuccessfulWork));
        await system.Start();
        await system.Queue.Enqueue("bulk.push.invalid");

        var outcome = await system.Workers.ExecuteAll(WorkAction.Push);

        Assert.Equal(1, outcome.MatchedWorkerCount);
        Assert.Equal(1, outcome.InvalidCount);
        Assert.Contains(outcome.Outcomes, actionOutcome => actionOutcome.Messages.Any(message => message.Code == "workable.worker.not_waiting"));
    }

    private static IWorkSystem CreateSystem(Action<IWorkSystemBuilder> configure)
        => new ServiceCollection()
            .AddWorkableSystem(configure)
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static WorkDefinition ManualDefinition(string name, string? category = null)
        => WorkDefinition.Create(
            name,
            category: category,
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });

    private static WorkDefinition RecurringDefinition(string name, string category)
        => WorkDefinition.Create(
            name,
            category: category,
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)),
            });

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static async Task<bool> WorkerIsWaiting(IWorkSystem system, WorkerId workerId)
        => (await system.Query.Worker(workerId))?.State == WorkerState.Waiting;

    private static async Task CancelIfActive(IWorkSystem system, WorkerId workerId)
    {
        var worker = await system.Query.Worker(workerId);
        if (worker is null || WorkerStateMachineIsFinal(worker.State))
        {
            return;
        }

        await system.Workers.Execute(worker.Version, WorkAction.Cancel);
    }

    private static bool WorkerStateMachineIsFinal(WorkerState state)
        => state is WorkerState.Canceled or WorkerState.Completed;

    private sealed class BulkActionTracker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PauseAwareWork(BulkActionTracker tracker) : IWorkExecutor
    {
        public async Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WorkExecutionResult.Success();
        }
    }
}

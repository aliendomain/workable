using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Start")]
public sealed class WorkStartConfigurationTests
{
    [Fact]
    public void DefaultStartPolicyReturnsAfterAccepted()
    {
        Assert.Equal(WorkStartPolicy.StartAndReturnAfterAccepted, WorkStartConfiguration.Default.Policy);
    }

    [Fact]
    public void AttributeConfiguresStartPolicy()
    {
        var definition = WorkDefinition.Create("attributed-start", "Uses start attribute.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedDoNotStartWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "attributed-start");

        Assert.Equal(WorkStartPolicy.DoNotStart, configured.Configuration.Start.Policy);
    }

    [Fact]
    public void BootstrapConfigurationCanConfigureStartPolicy()
    {
        var definition = WorkDefinition.Create("bootstrap-start", "Uses bootstrap config.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
                definition,
                configuration => configuration.ReturnAfterCompleted()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "bootstrap-start");

        Assert.Equal(WorkStartPolicy.StartAndReturnAfterCompleted, configured.Configuration.Start.Policy);
    }

    [Fact]
    public async Task DoNotStartQueuesWorkerWithoutStartingExecution()
    {
        var entered = false;
        var definition = WorkDefinition.Create("manual-start", "Queues without starting.",
            configuration: StartConfiguration(WorkStartPolicy.DoNotStart));
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            Volatile.Write(ref entered, true);
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("manual-start");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(WorkerState.Queued, worker.State);
        Assert.False(Volatile.Read(ref entered));
    }

    [Fact]
    public async Task QueueOptionsCanOverrideDefinitionStartPolicy()
    {
        var definition = WorkDefinition.Create("queue-start-override", "Queue options override start config.",
            configuration: StartConfiguration(WorkStartPolicy.DoNotStart));
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "queue-start-override",
            options: new WorkerOptions(Configuration: StartConfiguration(WorkStartPolicy.StartAndReturnAfterAccepted)));
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReturnAfterStartedWaitsUntilDeferredWorkerActuallyStarts()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("return-after-started", "Waits for deferred start.",
            configuration: StartConfiguration(WorkStartPolicy.StartAndReturnAfterStarted) with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Concurrency = WorkConcurrencyConfiguration.Default with
                    {
                        IsEnabled = true,
                        MaximumCapacity = 1,
                        LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                        OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
                    },
                },
            });
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("return-after-started");
        var secondQueueTask = system.Queue.Enqueue("return-after-started");

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TestEventually.Until(async () =>
        {
            var queued = await system.Query.Workers(new WorkerCriteria(
                DefinitionName: "return-after-started",
                States: new HashSet<WorkerState> { WorkerState.Queued },
                Take: 10));
            return queued.Workers.Count == 1;
        });

        Assert.False(secondQueueTask.IsCompleted);

        release.SetResult();
        var second = await secondQueueTask.WaitAsync(TimeSpan.FromSeconds(5));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.True(second.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task ReturnAfterCompletedWaitsForCompletionBeforeReturningHandle()
    {
        var entered = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("return-after-complete", "Waits for completion.",
            configuration: StartConfiguration(WorkStartPolicy.StartAndReturnAfterCompleted));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var queueTask = system.Queue.Enqueue("return-after-complete");

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(queueTask.IsCompleted);

        release.SetResult();
        var handle = await queueTask.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancelingQueueWaitAfterAcceptanceDoesNotCancelWorker()
    {
        var entered = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("cancel-queue-wait", "Queue wait cancellation does not cancel accepted work.",
            configuration: StartConfiguration(WorkStartPolicy.StartAndReturnAfterCompleted));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        using var queueCancellation = new CancellationTokenSource();
        var queueTask = system.Queue.Enqueue("cancel-queue-wait", cancellationToken: queueCancellation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queueCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queueTask);

        var worker = await TestEventually.UntilNotNull(async () =>
        {
            var workers = (await system.Query.Workers(new WorkerCriteria(Take: 10))).Workers;
            return workers.Count == 1 ? workers[0] : null;
        });

        release.SetResult();
        await TestEventually.Until(async () =>
        {
            var snapshot = await system.Query.Worker(worker.Id);
            return snapshot?.State == WorkerState.Completed;
        });
    }

    [Fact]
    public async Task RuntimeReconfigurationCanMoveQueuedWorkerToStartingPolicy()
    {
        var definition = WorkDefinition.Create("runtime-start", "Can change start policy while queued.",
            configuration: StartConfiguration(WorkStartPolicy.DoNotStart));
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-start");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Start: WorkStartConfiguration.Default));
        var completion = await handle.WaitForCompletion();

        Assert.True(outcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RuntimeReconfigurationCanMoveQueuedConcurrencyWorkerToStartingPolicy()
    {
        var definition = WorkDefinition.Create("runtime-concurrency-start", "Can change start policy while queued with concurrency enabled.",
            configuration: StartConfiguration(WorkStartPolicy.DoNotStart) with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Concurrency = WorkConcurrencyConfiguration.Default with
                    {
                        IsEnabled = true,
                        MaximumCapacity = 1,
                        LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                        OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
                    },
                },
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-concurrency-start");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Start: WorkStartConfiguration.Default));
        var completion = await handle.WaitForCompletion();

        Assert.True(outcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RuntimeReconfigurationDefersQueuedConcurrencyWorkerWhenCapacityIsFull()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var releaseFirst = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("runtime-concurrency-deferred-start", "Defers start reconfiguration until capacity is available.",
            configuration: StartConfiguration(WorkStartPolicy.DoNotStart) with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Concurrency = WorkConcurrencyConfiguration.Default with
                    {
                        IsEnabled = true,
                        MaximumCapacity = 1,
                        LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                        OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
                    },
                },
            });
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondStarted.SetResult();
            }

            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("runtime-concurrency-deferred-start");
        var second = await system.Queue.Enqueue("runtime-concurrency-deferred-start");
        var firstWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(first)));
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var firstStart = await system.Workers.Execute(firstWorker.Version, WorkAction.Start);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var reconfigureSecond = await system.Workers.Reconfigure(
            secondWorker.Version,
            new WorkerReconfiguration(Start: WorkStartConfiguration.Default));

        Assert.True(firstStart.IsAccepted);
        Assert.True(reconfigureSecond.IsAccepted);
        Assert.Equal(WorkerState.Queued, reconfigureSecond.Worker?.State);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    private static WorkConfiguration StartConfiguration(WorkStartPolicy policy)
        => WorkConfiguration.Default with
        {
            Start = new WorkStartConfiguration
            {
                Policy = policy,
            },
        };

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected the queue to accept a worker.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    [WorkStart(WorkStartPolicy.DoNotStart)]
    private sealed class AttributedDoNotStartWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class SuccessfulExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}

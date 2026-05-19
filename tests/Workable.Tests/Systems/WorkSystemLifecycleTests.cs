using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SystemLifecycle")]
public sealed class WorkSystemLifecycleTests
{
    [Fact]
    public void RegistryRejectsWhenNoSystemsAreRegistered()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() => new WorkSystemRegistry(
            services,
            [],
            [],
            [],
            [],
            []));

        Assert.Contains("At least one Workable system", exception.Message);
    }

    [Fact]
    public void RegistryRejectsDuplicateUnnamedDefaultSystems()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem(builder => builder.StartWithHost());

        var exception = Assert.Throws<InvalidOperationException>(() => services
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>());

        Assert.Contains("Only one unnamed default", exception.Message);
    }

    [Fact]
    public void RegistryRejectsDuplicateSystemNamesIgnoringCase()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem("email", builder => builder.StartWithHost())
            .AddWorkableSystem("EMAIL", builder => builder.StartWithHost());

        var exception = Assert.Throws<InvalidOperationException>(() => services
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>());

        Assert.Contains("Duplicate names", exception.Message);
        Assert.Contains("email", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistryUsesFirstSystemAsDefaultWhenNoUnnamedSystemExists()
    {
        var registry = new ServiceCollection()
            .AddWorkableSystem("first", builder => builder.StartWithHost())
            .AddWorkableSystem("second", builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>();

        Assert.Equal("first", registry.Default.Name);
    }

    [Fact]
    public void RegistryCanLookupSystemsByIdAndName()
    {
        var registry = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem("background", builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>();
        var background = registry.Systems.Single(system => system.Name == "background");

        Assert.True(registry.TryGet(registry.Default.Id, out var byId));
        Assert.Same(registry.Default, byId);
        Assert.True(registry.TryGet("BACKGROUND", out var byName));
        Assert.Same(background, byName);
        Assert.False(registry.TryGet(WorkSystemId.New(), out _));
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public void SystemRetentionDefaultsMatchConfiguredValues()
    {
        var retention = WorkSystemRetentionConfiguration.Default;

        Assert.Equal(10_000, retention.MaximumFinalWorkers);
    }

    [Fact]
    public void SystemCapacityDefaultsMatchConfiguredValues()
    {
        var capacity = WorkSystemCapacityConfiguration.Default;

        Assert.Equal(1_000_000, capacity.MaximumWorkers);
    }

    [Fact]
    public void SystemRetentionRejectsInvalidMaximumFinalWorkers()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.ConfigureRetention(maximumFinalWorkers: 0)));

        Assert.Contains("maximum final workers", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemCapacityRejectsInvalidMaximumWorkers()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.ConfigureCapacity(maximumWorkers: 0)));

        Assert.Contains("maximum workers", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemStartAndStopAreIdempotent()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        Assert.Equal(WorkSystemState.Created, system.State);

        await system.Start();
        await system.Start();

        Assert.Equal(WorkSystemState.Started, system.State);
        Assert.True(system.Catalog.IsFrozen);

        await system.Stop();
        await system.Stop();

        Assert.Equal(WorkSystemState.Stopped, system.State);
    }

    [Fact]
    public async Task HostedServiceStartsConfiguredSystemsAndStopsAllSystems()
    {
        var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem("manual", builder => { })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        Assert.True(registry.TryGet("manual", out var manual));

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Equal(WorkSystemState.Started, registry.Default.State);
        Assert.Equal(WorkSystemState.Created, manual.State);

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(WorkSystemState.Stopped, registry.Default.State);
        Assert.Equal(WorkSystemState.Stopped, manual.State);
    }

    [Fact]
    public async Task HostedServiceShutdownCompletesWhenHostCancellationTokenIsCanceled()
    {
        var tracker = new ShutdownTracker();
        var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .StartWithHost()
                .UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20))
                .AddWork<CancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.host-timeout")))
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        var handle = await registry.Default.Queue.Enqueue("shutdown.host-timeout");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var hostTimeout = new CancellationTokenSource();
        await hostTimeout.CancelAsync();
        var exception = await Record.ExceptionAsync(() => hostedService.StopAsync(hostTimeout.Token));
        var completion = await handle.WaitForCompletion();

        Assert.Null(exception);
        Assert.Equal(WorkSystemState.Stopped, registry.Default.State);
        Assert.Equal(WorkCompletionStatus.Interrupted, completion.Status);
    }

    [Fact]
    public async Task HostedServiceStopsSystemsConcurrently()
    {
        var tracker = new ConcurrentShutdownTracker(expectedStarts: 2);
        var provider = new ServiceCollection()
            .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(400))
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .StartWithHost()
                .AddWork<ConcurrentCancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.concurrent")))
            .AddWorkableSystem("remote", builder => builder
                .StartWithHost()
                .AddWork<ConcurrentCancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.concurrent")))
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        Assert.True(registry.TryGet("remote", out var remote));

        await hostedService.StartAsync(CancellationToken.None);
        var first = await registry.Default.Queue.Enqueue("shutdown.concurrent");
        var second = await remote.Queue.Enqueue("shutdown.concurrent");
        await tracker.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var startedAt = Stopwatch.GetTimestamp();
        await hostedService.StopAsync(CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal(WorkCompletionStatus.Interrupted, (await first.WaitForCompletion()).Status);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await second.WaitForCompletion()).Status);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(550), $"Expected systems to stop concurrently, but elapsed was {elapsed}.");
    }

    [Fact]
    public async Task DisposingSystemStopsItAndDisposesEventStream()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        await system.DisposeAsync();

        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Throws<ObjectDisposedException>(() => system.Events.Subscribe());
    }

    [Fact]
    public async Task StopRejectsIncomingWorkWhileShutdownIsInProgress()
    {
        var tracker = new ShutdownTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .UseShutdownGracePeriod(TimeSpan.FromSeconds(5))
                .AddWork<SlowCancelShutdownWork>(WorkDefinition.Create("shutdown.slow-cancel")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var running = await system.Queue.Enqueue("shutdown.slow-cancel");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = system.Stop();
        await tracker.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var rejected = await system.Queue.Enqueue("shutdown.slow-cancel");
        tracker.ReleaseCancel.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(running.QueueOutcome.IsAccepted);
        Assert.Equal(WorkQueueStatus.Invalid, rejected.QueueOutcome.Status);
        Assert.Contains(rejected.QueueOutcome.Messages, message => message.Code == "workable.system.stopping");
    }

    [Fact]
    public async Task QueueRejectsWorkWhenSystemIsNotStarted()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("lifecycle.not-started"),
                (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var created = await system.Queue.Enqueue("lifecycle.not-started");
        await system.Start();
        await system.Stop();
        var stopped = await system.Queue.Enqueue("lifecycle.not-started");

        Assert.Equal(WorkQueueStatus.Invalid, created.QueueOutcome.Status);
        Assert.Contains(created.QueueOutcome.Messages, message => message.Code == "workable.system.not_started");
        Assert.Equal(WorkQueueStatus.Invalid, stopped.QueueOutcome.Status);
        Assert.Contains(stopped.QueueOutcome.Messages, message => message.Code == "workable.system.not_started");
    }

    [Fact]
    public async Task StopRequestsInterruptionAndWaitsForCooperativeWork()
    {
        var tracker = new ShutdownTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .UseShutdownGracePeriod(TimeSpan.FromSeconds(5))
                .AddWork<CancelAwareShutdownWork>(WorkDefinition.Create("shutdown.cancel-aware")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("shutdown.cancel-aware");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await system.Stop();
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Interrupted, completion.Status);
        Assert.True(tracker.CancellationObserved.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopClearsWorkerMemoryAfterShutdown()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(WorkDefinition.Create("shutdown.completed"), (context, input, cancellationToken) =>
                    Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWork(WorkDefinition.Create("shutdown.failed"), (context, input, cancellationToken) =>
                    Task.FromResult(WorkExecutionResult.Failure(
                        [WorkMessage.Error("shutdown.failed", "The worker failed before shutdown.")])));
                builder.AddWork(
                    WorkDefinition.Create(
                        "shutdown.queued",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success()));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var completed = await system.Queue.Enqueue("shutdown.completed");
        await completed.WaitForCompletion();
        var handle = await system.Queue.Enqueue(
            "shutdown.failed",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("shutdown-test", "failed")));
        var failed = await handle.WaitForCompletion();
        var queued = await system.Queue.Enqueue("shutdown.queued");

        await system.Stop();
        var completedWorker = await system.Query.Worker(completed.WorkerId ?? throw new InvalidOperationException("Expected completed worker id."));
        var failedWorker = await system.Query.Worker(handle.WorkerId ?? throw new InvalidOperationException("Expected failed worker id."));
        var queuedWorker = await system.Query.Worker(queued.WorkerId ?? throw new InvalidOperationException("Expected queued worker id."));
        var overview = await system.Query.SystemDetails();
        var query = await system.Query.Workers(new WorkerCriteria());
        var keys = await system.Query.WorkerKeys(new WorkerKeyCriteria(Search: "shutdown-test"));

        Assert.Equal(WorkCompletionStatus.Failed, failed.Status);
        Assert.Null(completedWorker);
        Assert.Null(failedWorker);
        Assert.Null(queuedWorker);
        Assert.Empty(query.Workers);
        Assert.Empty(keys.Keys);
        Assert.Equal(0, overview.ActiveWorkerCount);
        Assert.Equal(0, overview.FinalWorkerCount);
        Assert.Equal(0, overview.FailedWorkerCount);
        Assert.Empty(overview.WorkerCountByState);
        Assert.Empty(overview.FailedWorkers);
        Assert.Empty(overview.FailedIterations);
        Assert.Empty(overview.CompletedIterations);
    }

    [Fact]
    public async Task StopClearsThroughputMetricsAfterShutdown()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(WorkDefinition.Create("shutdown.metrics"), (context, input, cancellationToken) =>
                    Task.FromResult(WorkExecutionResult.Success()));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("shutdown.metrics");
        await handle.WaitForCompletion();

        var beforeStop = await system.Query.SystemThroughput(
            new WorkSystemCriteria(),
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1));

        await system.Stop();

        var afterStop = await system.Query.SystemThroughput(
            new WorkSystemCriteria(),
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1));

        Assert.Equal(1 / 60.0, beforeStop.LiveSummary.StartedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, beforeStop.LiveSummary.CompletedPerSecond, precision: 6);
        Assert.Empty(afterStop.Buckets);
        Assert.Equal(0, afterStop.ExecutionSummary.ExecutionCount);
        Assert.Equal(0, afterStop.LiveSummary.StartedPerSecond);
        Assert.Equal(0, afterStop.LiveSummary.CompletedPerSecond);
    }

    [Fact]
    public async Task StopForceInterruptsWorkAfterGracePeriod()
    {
        var tracker = new ShutdownTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20))
                .AddWork<CancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.ignores-cancel")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("shutdown.ignores-cancel");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = await system.Stop();
        var completion = await handle.WaitForCompletion();

        var canceled = Assert.Single(stop.CancellationRequestedWorkers);
        var forceCanceled = Assert.Single(stop.ForceCanceledWorkers);
        var forceCanceledSummary = Assert.Single(stop.ForceCanceledWorkerSummaries);
        Assert.Equal(handle.WorkerId, canceled.Id);
        Assert.Equal(handle.WorkerId, forceCanceled.Id);
        Assert.Equal(handle.WorkerId, forceCanceledSummary.Id);
        Assert.Equal("shutdown.ignores-cancel", forceCanceledSummary.DefinitionName);
        Assert.Equal(["shutdown.ignores-cancel"], stop.ForceCanceledWorkerNames);
        Assert.Equal(TimeSpan.FromMilliseconds(20), stop.ShutdownGracePeriod);
        Assert.Equal(WorkCompletionStatus.Interrupted, completion.Status);
        Assert.Equal(WorkerState.Interrupted, completion.Worker?.State);
        Assert.Contains(completion.Messages, message => message.Code == "workable.worker.shutdown_interrupted_forced");
    }

    [Fact]
    public async Task DefaultShutdownGracePeriodUsesHostShutdownTimeoutRatio()
    {
        var tracker = new ShutdownTracker();
        var provider = new ServiceCollection()
            .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200))
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .AddWork<CancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.host-ratio-default")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("shutdown.host-ratio-default");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var startedAt = Stopwatch.GetTimestamp();
        var stop = await system.Stop();
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Single(stop.ForceCanceledWorkers);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await handle.WaitForCompletion()).Status);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(120), $"Expected host-relative grace wait, but elapsed was {elapsed}.");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Expected bounded shutdown, but elapsed was {elapsed}.");
    }

    [Fact]
    public async Task ExplicitShutdownGracePeriodOverridesHostShutdownTimeoutRatio()
    {
        var tracker = new ShutdownTracker();
        var provider = new ServiceCollection()
            .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5))
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20))
                .AddWork<CancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.explicit-grace")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("shutdown.explicit-grace");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var startedAt = Stopwatch.GetTimestamp();
        var stop = await system.Stop();
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Single(stop.ForceCanceledWorkers);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await handle.WaitForCompletion()).Status);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Expected explicit grace period to win, but elapsed was {elapsed}.");
    }

    [Fact]
    public void ShutdownGracePeriodRatioRejectsValuesAboveNinetyPercent()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseShutdownGracePeriodRatio(0.91)));

        Assert.Equal("hostShutdownTimeoutRatio", exception.ParamName);
    }

    [Fact]
    public async Task StopInterruptsLargeDeferredConcurrencyBacklog()
    {
        var tracker = new ShutdownTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20))
                .AddWork<CancelAwareShutdownWork>(
                    WorkDefinition.Create(
                        "shutdown.deferred-backlog",
                        configuration: WorkConfiguration.Default with
                        {
                            Coordination = WorkCoordinationConfiguration.Default with
                            {
                                IsEnabled = true,
                                Concurrency = WorkConcurrencyConfiguration.Default with
                                {
                                    IsEnabled = true,
                                    MaximumCapacity = 1,
                                    BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                                    LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                                },
                            },
                        })))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var running = await system.Queue.Enqueue("shutdown.deferred-backlog");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deferred = new List<IWorkerHandle>();
        for (var i = 0; i < 500; i++)
        {
            deferred.Add(await system.Queue.Enqueue("shutdown.deferred-backlog"));
        }

        await system.Stop().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkCompletionStatus.Interrupted, (await running.WaitForCompletion()).Status);
        foreach (var handle in deferred)
        {
            Assert.Equal(WorkCompletionStatus.Interrupted, (await handle.WaitForCompletion()).Status);
        }
    }

    private sealed class ShutdownTracker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ConcurrentShutdownTracker(int expectedStarts)
    {
        private int startedCount;

        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalStarted()
        {
            if (Interlocked.Increment(ref this.startedCount) == expectedStarts)
            {
                this.AllStarted.TrySetResult();
            }
        }
    }

    private sealed class ConcurrentCancellationIgnoringShutdownWork(ConcurrentShutdownTracker tracker) : IWorkExecutor
    {
        public async Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.SignalStarted();
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            return WorkExecutionResult.Success();
        }
    }

    private sealed class CancelAwareShutdownWork(ShutdownTracker tracker) : IWorkExecutor
    {
        public async Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorkExecutionResult.Success();
            }
            catch (OperationCanceledException)
            {
                tracker.CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class SlowCancelShutdownWork(ShutdownTracker tracker) : IWorkExecutor
    {
        public async Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorkExecutionResult.Success();
            }
            catch (OperationCanceledException)
            {
                tracker.CancellationObserved.TrySetResult();
                await tracker.ReleaseCancel.Task;
                throw;
            }
        }
    }

    private sealed class CancellationIgnoringShutdownWork(ShutdownTracker tracker) : IWorkExecutor
    {
        public async Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            return WorkExecutionResult.Success();
        }
    }
}

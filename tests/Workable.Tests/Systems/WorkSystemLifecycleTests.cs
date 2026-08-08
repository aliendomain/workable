using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public void RegistryCanLookupNamedSystemsByName()
    {
        var registry = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem("background", builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>();
        var background = registry.Systems.Single(system => system.Name == "background");

        Assert.Null(registry.Default.Name);
        Assert.True(registry.TryGet("BACKGROUND", out var byBackgroundName));
        Assert.Same(background, byBackgroundName);
        Assert.False(registry.TryGet("missing-system", out _));
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
    public void SystemIterationStatusDefaultsMatchConfiguredValues()
    {
        var configuration = WorkSystemIterationStatusConfiguration.Default;

        Assert.Equal(4_096, configuration.ReplayItemCapacity);
        Assert.Equal(4 * 1_024 * 1_024, configuration.ReplayPayloadByteCapacity);
        Assert.Equal(65_536, configuration.SystemReplayItemCapacity);
        Assert.Equal(64 * 1_024 * 1_024, configuration.SystemReplayByteCapacity);
        Assert.Equal(32 * 1_024, configuration.MaximumPayloadBytes);
        Assert.Equal(256, configuration.MaximumTypeBytes);
        Assert.Equal(4_096, configuration.MaximumSubscriptions);
        Assert.Equal(64, configuration.MaximumSubscriptionsPerIteration);
    }

    [Fact]
    public void SystemProfilingDefaultsMatchConfiguredValues()
    {
        var profiling = WorkSystemProfilingConfiguration.Default;

        Assert.Equal(500, profiling.MaximumAutomaticInstrumentationNodes);
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

    [Theory]
    [InlineData(0, 1, "replay item capacity")]
    [InlineData(1, 0, "maximum payload bytes")]
    public void SystemIterationStatusesRejectInvalidLimits(
        int replayItemCapacity,
        int maximumPayloadBytes,
        string expectedMessage)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.ConfigureIterationStatuses(
                replayItemCapacity: replayItemCapacity,
                maximumPayloadBytes: maximumPayloadBytes)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemIterationStatusesRejectInvalidReplacementConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(null!)));
        var invalidReplay = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                ReplayItemCapacity = 0,
            })));
        var invalidPayload = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                MaximumPayloadBytes = 0,
            })));
        var invalidReplayBytes = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                ReplayPayloadByteCapacity = 0,
            })));
        var inconsistentPayloadLimits = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                ReplayPayloadByteCapacity = 1_024,
                MaximumPayloadBytes = 2_048,
            })));
        var invalidType = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                MaximumTypeBytes = 0,
            })));
        var invalidSystemItems = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                SystemReplayItemCapacity = 1,
            })));
        var invalidSystemBytes = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                SystemReplayByteCapacity = 1,
            })));
        var invalidIterationSubscriptions = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                MaximumSubscriptionsPerIteration = 0,
            })));
        var invalidSystemSubscriptions = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseIterationStatuses(new WorkSystemIterationStatusConfiguration
            {
                MaximumSubscriptions = 1,
            })));

        Assert.Contains("replay item capacity", invalidReplay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum payload bytes", invalidPayload.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replay payload byte capacity", invalidReplayBytes.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be less", inconsistentPayloadLimits.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum type bytes", invalidType.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system replay item capacity", invalidSystemItems.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system replay byte capacity", invalidSystemBytes.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subscriptions per iteration", invalidIterationSubscriptions.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum subscriptions", invalidSystemSubscriptions.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemProfilingRejectsInvalidAutomaticInstrumentationLimit()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.ConfigureProfiling(maximumAutomaticInstrumentationNodes: 0)));

        Assert.Contains("automatic instrumentation nodes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemProfilingUsesConfiguredAutomaticInstrumentationLimit()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.UseProfiling(new WorkSystemProfilingConfiguration
            {
                MaximumAutomaticInstrumentationNodes = 17,
            }))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        Assert.Equal(
            17,
            Assert.IsAssignableFrom<IWorkProfileCaptureRuleSystem>(system)
                .ProfilingConfiguration.MaximumAutomaticInstrumentationNodes);
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
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .StartWithHost()
                .UseShutdownGracePeriod(TimeSpan.FromSeconds(30))
                .AddWork<ConcurrentCancelAwareShutdownWork>(WorkDefinition.Create("shutdown.concurrent")))
            .AddWorkableSystem("remote", builder => builder
                .StartWithHost()
                .UseShutdownGracePeriod(TimeSpan.FromSeconds(30))
                .AddWork<ConcurrentCancelAwareShutdownWork>(WorkDefinition.Create("shutdown.concurrent")))
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        Assert.True(registry.TryGet("remote", out var remote));

        await hostedService.StartAsync(CancellationToken.None);
        var first = await registry.Default.Queue.Enqueue("shutdown.concurrent");
        var second = await remote.Queue.Enqueue("shutdown.concurrent");
        await tracker.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = hostedService.StopAsync(CancellationToken.None);
        try
        {
            await tracker.AllCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            tracker.ReleaseCancel.TrySetResult();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkCompletionStatus.Interrupted, (await first.WaitForCompletion()).Status);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await second.WaitForCompletion()).Status);
    }

    [Fact]
    public async Task HostedServiceLogsTheShutdownPlanAndForcedInterruptionsWithoutFloodingWorkerDetails()
    {
        var tracker = new ShutdownTracker();
        var logs = new CapturingLoggerProvider();
        var provider = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(logs))
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .StartWithHost()
                .UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20))
                .AddWork<CancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.logged")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        var first = await system.Queue.Enqueue("shutdown.logged");
        var second = await system.Queue.Enqueue("shutdown.logged");
        await TestEventually.Until(async () =>
        {
            var workers = await system.Query.Workers(new WorkerCriteria(
                States: new HashSet<WorkerState> { WorkerState.Running }));
            return workers.TotalCount == 2;
        });

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(WorkCompletionStatus.Interrupted, (await first.WaitForCompletion()).Status);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await second.WaitForCompletion()).Status);
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("Workable shutdown started", StringComparison.Ordinal) &&
            entry.Message.Contains("Workers to stop: 2", StringComparison.Ordinal) &&
            entry.Message.Contains("default 0:00:00.02", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("Stopping workers", StringComparison.Ordinal) &&
            entry.Message.Contains("shutdown.logged x2", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Force-interrupted 2 worker(s)", StringComparison.Ordinal) &&
            entry.Message.Contains("shutdown.logged x2", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("shutdown complete: 1 system(s), 2 cooperative cancellation(s)", StringComparison.OrdinalIgnoreCase));
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
        await TestEventually.Until(async () =>
            (await system.Query.Workers(new WorkerCriteria())).Workers.Count == 0);
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
        var forceInterrupted = Assert.Single(stop.ForceInterruptedWorkers);
        var forceInterruptedSummary = Assert.Single(stop.ForceInterruptedWorkerSummaries);
        Assert.Equal(handle.WorkerId, canceled.Id);
        Assert.Equal(handle.WorkerId, forceInterrupted.Id);
        Assert.Equal(handle.WorkerId, forceInterruptedSummary.Id);
        Assert.Equal("shutdown.ignores-cancel", forceInterruptedSummary.DefinitionName);
        Assert.Equal(["shutdown.ignores-cancel"], stop.ForceInterruptedWorkerNames);
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
            .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(50))
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .AddWork<CancellationIgnoringShutdownWork>(WorkDefinition.Create("shutdown.host-ratio-default")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("shutdown.host-ratio-default");
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = await system.Stop();

        Assert.Single(stop.ForceInterruptedWorkers);
        Assert.Equal(TimeSpan.FromMilliseconds(40), stop.ShutdownGracePeriod);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await handle.WaitForCompletion()).Status);
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

        var stop = await system.Stop();

        Assert.Single(stop.ForceInterruptedWorkers);
        Assert.Equal(TimeSpan.FromMilliseconds(20), stop.ShutdownGracePeriod);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await handle.WaitForCompletion()).Status);
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
        private int cancellationObservedCount;

        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalStarted()
        {
            if (Interlocked.Increment(ref this.startedCount) == expectedStarts)
            {
                this.AllStarted.TrySetResult();
            }
        }

        public void SignalCancellationObserved()
        {
            if (Interlocked.Increment(ref this.cancellationObservedCount) == expectedStarts)
            {
                this.AllCancellationObserved.TrySetResult();
            }
        }
    }

    private sealed class ConcurrentCancelAwareShutdownWork(ConcurrentShutdownTracker tracker) : IWorkExecutor
    {
        public async Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.SignalStarted();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorkExecutionResult.Success();
            }
            catch (OperationCanceledException)
            {
                tracker.SignalCancellationObserved();
                await tracker.ReleaseCancel.Task;
                throw;
            }
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

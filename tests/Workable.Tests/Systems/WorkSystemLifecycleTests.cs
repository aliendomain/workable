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
    public async Task StopRequestsCancellationAndWaitsForCooperativeWork()
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

        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.True(tracker.CancellationObserved.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopForceCancelsWorkAfterGracePeriod()
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

        await system.Stop();
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.Equal(WorkerState.Canceled, completion.Worker?.State);
        Assert.Contains(completion.Messages, message => message.Code == "workable.worker.shutdown_forced");
    }

    private sealed class ShutdownTracker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return WorkExecutionResult.Success();
        }
    }
}

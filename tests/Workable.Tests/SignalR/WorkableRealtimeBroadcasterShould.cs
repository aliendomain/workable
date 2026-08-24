using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeBroadcasterShould
{
    [Fact]
    public void NormalizeDiagnosticAlertCriteriaAndEventTimingBoundaries()
    {
        Assert.False(InvokeStatic<bool>(
            "IsDiagnosticsAlertChangesSubscription",
            ViewSubscription(new WorkViewCriteria(Components: null))));

        foreach (var componentType in new[]
        {
            "queueDiagnostics",
            "systemDiagnostics",
            "readModelDiagnostics",
            "retentionDiagnostics",
            "concurrencyDiagnostics",
            "durabilityDiagnostics",
        })
        {
            var criteria = new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "diagnostics",
                    componentType,
                    JsonSerializer.SerializeToElement(new { publishMode = "alertChanges" }),
                    WorkComponentShapes.Compact),
            ]);
            Assert.True(InvokeStatic<bool>(
                "IsDiagnosticsAlertChangesSubscription",
                ViewSubscription(criteria)));
        }

        Assert.False(InvokeStatic<bool>(
            "IsDiagnosticsAlertChangesSubscription",
            ViewSubscription(new WorkViewCriteria(Components:
            [
                new("diagnostics", "futureDiagnostics", JsonSerializer.SerializeToElement(new { publishMode = "alertChanges" }), WorkComponentShapes.Compact),
                new("wrong-shape", "queueDiagnostics", JsonSerializer.SerializeToElement(new { publishMode = "alertChanges" }), WorkComponentShapes.Standard),
                new("wrong-mode", "queueDiagnostics", JsonSerializer.SerializeToElement(new { publishMode = "interval" }), WorkComponentShapes.Compact),
            ]))));

        var configured = ViewSubscription(new WorkViewCriteria(Components:
        [
            new("read", "readModelDiagnostics", JsonSerializer.SerializeToElement(new { warningThreshold = 0 })),
            new("retention", "retentionDiagnostics", JsonSerializer.SerializeToElement(new { warningSeconds = 2 })),
            new("concurrency", "concurrencyDiagnostics", JsonSerializer.SerializeToElement(new { warningSeconds = 3 })),
            new("durability", "durabilityDiagnostics", JsonSerializer.SerializeToElement(new
            {
                acceptedWorkerWarningSeconds = 4,
                cleanupWarningSeconds = 5,
            })),
        ]));
        Assert.Equal(1, InvokeStatic<int>("GetReadModelDiagnosticsWarningThreshold", configured));
        Assert.Equal(2, InvokeStatic<int>("GetRetentionDiagnosticsWarningSeconds", configured));
        Assert.Equal(3, InvokeStatic<int>("GetConcurrencyDiagnosticsWarningSeconds", configured));
        Assert.Equal(4, InvokeStatic<int>("GetDurabilityAcceptedWorkerWarningSeconds", configured));
        Assert.Equal(5, InvokeStatic<int>("GetDurabilityCleanupWarningSeconds", configured));

        var defaults = ViewSubscription(new WorkViewCriteria(Components: null));
        Assert.Equal(100, InvokeStatic<int>("GetReadModelDiagnosticsWarningThreshold", defaults));
        Assert.Equal(30, InvokeStatic<int>("GetRetentionDiagnosticsWarningSeconds", defaults));
        Assert.Equal(30, InvokeStatic<int>("GetConcurrencyDiagnosticsWarningSeconds", defaults));
        Assert.Equal(30, InvokeStatic<int>("GetDurabilityAcceptedWorkerWarningSeconds", defaults));
        Assert.Equal(30, InvokeStatic<int>("GetDurabilityCleanupWarningSeconds", defaults));

        var timingOptions = new WorkableSignalROptions
        {
            MinimumTimeWindow = TimeSpan.FromMilliseconds(50),
            BatchTimeWindow = TimeSpan.Zero,
            LiveTimeWindow = TimeSpan.FromMilliseconds(1),
        };
        Assert.Equal(TimeSpan.FromSeconds(1), InvokeStatic<TimeSpan>("NormalizeBatchTimeWindow", timingOptions));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            InvokeStatic<TimeSpan>("NormalizeBatchTimeWindow", new WorkableSignalROptions
            {
                MinimumTimeWindow = TimeSpan.FromSeconds(2),
                BatchTimeWindow = TimeSpan.FromSeconds(1),
            }));
        Assert.Equal(TimeSpan.FromMilliseconds(50), InvokeStatic<TimeSpan>("NormalizeLiveTimeWindow", timingOptions));
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            InvokeStatic<TimeSpan>(
                "ResolveTimeWindow",
                EventSubscription() with { Filter = new WorkEventFilter(WorkerId: WorkerId.New()) },
                timingOptions));
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            InvokeStatic<TimeSpan>("ResolveTimeWindow", EventSubscription(), timingOptions));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 2)]
    public void ClassifyEveryDiagnosticsLagBoundary(
        int thresholdMultiple,
        int expectedValue)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            warningThreshold = 1,
            warningSeconds = 1,
            acceptedWorkerWarningSeconds = 1,
            cleanupWarningSeconds = 1,
        });
        var subscription = ViewSubscription(new WorkViewCriteria(Components:
        [
            new("read", "readModelDiagnostics", options),
            new("retention", "retentionDiagnostics", options),
            new("concurrency", "concurrencyDiagnostics", options),
            new("durability", "durabilityDiagnostics", options),
        ]));
        var state = InvokeStatic<WorkableRealtimeDiagnosticsAlertState>(
            "CreateDiagnosticsAlertState",
            new DiagnosticsSession(CreateDiagnostics(thresholdMultiple)),
            subscription,
            null);
        var expected = (WorkableRealtimeDiagnosticsLagSeverity)expectedValue;

        Assert.Equal(expected, state.ReadModelLagSeverity);
        Assert.Equal(expected, state.RetentionLagSeverity);
        Assert.Equal(expected, state.ConcurrencyLagSeverity);
        Assert.Equal(expected, state.AcceptedWorkerLagSeverity);
        Assert.Equal(expected, state.CleanupLagSeverity);
    }

    [Fact]
    public async Task KeepEmptyAuthorizedEventPumpInertUntilItsGroupIsCanceled()
    {
        await using var provider = CreateAuthorizedProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var eventSubscriptions = new WorkableRealtimeEventSubscriptions();
        var broadcaster = CreateBroadcaster(
            provider,
            new WorkableRealtimeViewSubscriptions(),
            new RecordingHubClients("never-fails"),
            new ManualTimerFactory(),
            new RecordingLogger<WorkableRealtimeBroadcaster>(),
            eventSubscriptions: eventSubscriptions);
        var subscription = new WorkableRealtimeEventSubscriptions.EventSubscription(
            "hidden-connection",
            system.Id,
            "hidden-events",
            Filter: null,
            NoReadAuthorization());
        using var cancellation = new CancellationTokenSource();

        var pump = InvokeTask(
            broadcaster,
            "BroadcastEventGroup",
            system,
            subscription,
            cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(pump.IsCompleted);

        await cancellation.CancelAsync();
        await pump.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KeepEmptyAuthorizedWorkerOverviewPumpInertUntilItsGroupIsCanceled()
    {
        await using var provider = CreateAuthorizedProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        using var watchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = subscriptions.Watch(
            "hidden-connection",
            groups,
            system,
            "hidden-worker",
            WorkerId.New(),
            new WorkWorkerOverviewRealtimeCriteria(),
            NoReadAuthorization(),
            watchCancellation.Token);
        var subscription = await TestEventually.UntilNotNull(
            () => Task.FromResult(subscriptions.GetActiveSubscriptions(system).SingleOrDefault()),
            "Expected the hidden worker overview subscription to become active.");
        subscriptions.SetSeeded(subscription.GroupName, hasPublishedState: false);
        var broadcaster = CreateBroadcaster(
            provider,
            new WorkableRealtimeViewSubscriptions(),
            new RecordingHubClients("never-fails"),
            new ManualTimerFactory(),
            new RecordingLogger<WorkableRealtimeBroadcaster>(),
            workerOverviewSubscriptions: subscriptions);
        using var cancellation = new CancellationTokenSource();

        var pump = InvokeTask(
            broadcaster,
            "BroadcastWorkerOverviewGroup",
            system,
            subscription,
            cancellation.Token);
        await watch.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(pump.IsCompleted);

        await cancellation.CancelAsync();
        await pump.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task IsolateDiagnosticsDeliveryFailuresAndForgetInactiveGroups()
    {
        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        var authorization = Authorization();
        var alertOptions = System.Text.Json.JsonSerializer.SerializeToElement(new { publishMode = "alertChanges" });
        var failed = await subscriptions.WatchView(
            "failed-connection",
            groups,
            system,
            "failed-alert",
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new("queue", "queueDiagnostics", alertOptions, WorkComponentShapes.Compact),
            ]),
            authorization,
            CancellationToken.None);
        var healthy = await subscriptions.WatchView(
            "healthy-connection",
            groups,
            system,
            "healthy-alert",
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new("system", "systemDiagnostics", alertOptions, WorkComponentShapes.Compact),
            ]),
            authorization,
            CancellationToken.None);
        subscriptions.CompleteSeed(failed.GroupName);
        subscriptions.CompleteSeed(healthy.GroupName);
        var clients = new RecordingHubClients("failed-connection");
        var timerFactory = new ManualTimerFactory();
        var logger = new RecordingLogger<WorkableRealtimeBroadcaster>();
        var broadcaster = CreateBroadcaster(
            provider,
            subscriptions,
            clients,
            timerFactory,
            logger);
        using var cancellation = new CancellationTokenSource();

        var accepted = await system.Queue.Enqueue("signalr.broadcaster.tests");
        Assert.True(accepted.QueueOutcome.IsAccepted);
        var rejected = await system.Queue.Enqueue("signalr.broadcaster.tests");
        Assert.False(rejected.QueueOutcome.IsAccepted);

        var broadcast = InvokeAsync(
            broadcaster,
            "BroadcastDiagnosticsViews",
            system,
            cancellation.Token);
        await timerFactory.Timer.WaitForWaitCount(1);
        timerFactory.Timer.Tick();
        await TestEventually.Until(
            () => clients.For("healthy-connection").Calls.Count == 1 && logger.Entries.Count == 1,
            "Expected a failed diagnostics client to be isolated from the healthy client.");

        await timerFactory.Timer.WaitForWaitCount(2);
        timerFactory.Timer.Tick();
        await timerFactory.Timer.WaitForWaitCount(3);

        Assert.InRange(clients.For("healthy-connection").Calls.Count, 1, 2);
        Assert.Equal(2, clients.For("failed-connection").Attempts);

        await subscriptions.UnwatchView(
            "healthy-connection",
            groups,
            system,
            healthy.SubscriptionId,
            CancellationToken.None);
        timerFactory.Timer.Tick();
        await timerFactory.Timer.WaitForWaitCount(4);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broadcast);

        Assert.Equal(3, clients.For("failed-connection").Attempts);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Contains(failed.GroupName, entry.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsolateOrdinaryViewFailuresAndDropStaleVersionBookkeeping()
    {
        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        var authorization = Authorization();
        var failed = await subscriptions.WatchView(
            "failed-connection",
            groups,
            system,
            "failed-throughput",
            "overview",
            new WorkViewCriteria(Components:
            [
                new("failed-throughput", "throughput", Shape: WorkComponentShapes.Compact),
            ]),
            authorization,
            CancellationToken.None);
        var healthy = await subscriptions.WatchView(
            "healthy-connection",
            groups,
            system,
            "healthy-throughput",
            "overview",
            new WorkViewCriteria(Components:
            [
                new("healthy-throughput", "throughput", Shape: WorkComponentShapes.Standard),
            ]),
            authorization,
            CancellationToken.None);
        subscriptions.CompleteSeed(failed.GroupName);
        subscriptions.CompleteSeed(healthy.GroupName);
        var clients = new RecordingHubClients("failed-connection");
        var logger = new RecordingLogger<WorkableRealtimeBroadcaster>();
        var broadcaster = CreateBroadcaster(
            provider,
            subscriptions,
            clients,
            new ManualTimerFactory(),
            logger);
        var versions = new Dictionary<string, WorkableRealtimeViewVersion>(StringComparer.Ordinal)
        {
            ["inactive-group"] = new(10, 20),
        };

        await InvokeAsync(
            broadcaster,
            "BroadcastViewSubscriptions",
            system,
            new Func<WorkableRealtimeViewSubscription, bool>(_ => true),
            versions,
            CancellationToken.None);

        Assert.DoesNotContain("inactive-group", versions);
        Assert.Single(clients.For("healthy-connection").Calls);
        Assert.Equal(1, clients.For("failed-connection").Attempts);
        var error = Assert.Single(logger.Entries);
        Assert.Contains("overview", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeferOnlyTheSeedingGroupAndReconcileItAfterItsSeedCompletes()
    {
        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        var seeding = await subscriptions.WatchView(
            "seeding-connection",
            groups,
            system,
            "seeding-view",
            "overview",
            new WorkViewCriteria(Components:
            [
                new("seeding-throughput", "throughput", Shape: WorkComponentShapes.Compact),
            ]),
            Authorization(),
            CancellationToken.None);
        var healthy = await subscriptions.WatchView(
            "healthy-connection",
            groups,
            system,
            "healthy-view",
            "overview",
            new WorkViewCriteria(Components:
            [
                new("healthy-throughput", "throughput", Shape: WorkComponentShapes.Standard),
            ]),
            Authorization(),
            CancellationToken.None);
        subscriptions.CompleteSeed(healthy.GroupName);
        var clients = new RecordingHubClients("never-fails");
        var broadcaster = CreateBroadcaster(
            provider,
            subscriptions,
            clients,
            new ManualTimerFactory(),
            new RecordingLogger<WorkableRealtimeBroadcaster>());
        var versions = new Dictionary<string, WorkableRealtimeViewVersion>(StringComparer.Ordinal);

        await InvokeAsync(
            broadcaster,
            "BroadcastViewSubscriptions",
            system,
            new Func<WorkableRealtimeViewSubscription, bool>(_ => true),
            versions,
            CancellationToken.None);

        Assert.Empty(clients.For("seeding-connection").Calls);
        Assert.Single(clients.For("healthy-connection").Calls);

        subscriptions.CompleteSeed(seeding.GroupName);
        var ready = await subscriptions.WaitForSeedReconciliations(system, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await InvokeAsync(
            broadcaster,
            "ReconcileSeededViewSubscriptions",
            system,
            ready,
            versions,
            CancellationToken.None);

        Assert.Single(clients.For("seeding-connection").Calls);
        Assert.Single(clients.For("healthy-connection").Calls);
    }

    [Fact]
    public async Task ReconcileActiveViewsWhenTheSharedChangeStreamRestarts()
    {
        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var subscription = await subscriptions.WatchView(
            "restart-connection",
            new RecordingSignalRGroupManager(),
            system,
            "restart-view",
            "workers",
            new WorkViewCriteria(),
            Authorization(),
            CancellationToken.None);
        subscriptions.CompleteSeed(subscription.GroupName);
        var clients = new RecordingHubClients("never-fails");
        var broadcaster = CreateBroadcaster(
            provider,
            subscriptions,
            clients,
            new ManualTimerFactory(),
            new RecordingLogger<WorkableRealtimeBroadcaster>());

        await InvokeAsync(
            broadcaster,
            "BroadcastViewsFromChanges",
            system,
            new CompletedChangeStream(),
            CancellationToken.None);
        Assert.Empty(clients.For("restart-connection").Calls);

        var handle = await system.Queue.Enqueue("signalr.broadcaster.tests");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected an accepted worker.");
        await TestEventually.Until(async () => await system.Query.Worker(workerId) is not null);

        await InvokeAsync(
            broadcaster,
            "BroadcastViewsFromChanges",
            system,
            new CompletedChangeStream(),
            CancellationToken.None);

        Assert.Single(clients.For("restart-connection").Calls);
    }

    [Fact]
    public async Task CompleteBatchCollectionWhenTheUnderlyingStreamEnds()
    {
        var broadcaster = CreateBatchingBroadcaster(maxBatchSize: 3);
        var firstEvent = CreateEvent("first");
        var eventReader = new SequenceAsyncEnumerator<WorkEvent>();
        var eventSubscription = EventSubscription();

        var eventBatch = await InvokeWithResult(
            broadcaster,
            "CollectEventBatch",
            eventSubscription,
            eventReader,
            null,
            firstEvent,
            CancellationToken.None);
        var batchEvents = Assert.IsAssignableFrom<IReadOnlyList<WorkEvent>>(
            eventBatch.GetType().GetProperty("Events")!.GetValue(eventBatch));
        Assert.Equal([firstEvent], batchEvents);
        Assert.Null(eventBatch.GetType().GetProperty("PendingRead")!.GetValue(eventBatch));

        var changeReader = new SequenceAsyncEnumerator<WorkChange>();
        var changedKeys = new HashSet<WorkChangeKey> { WorkChangeKey.System() };
        Assert.Null(await InvokeWithResult(
            broadcaster,
            "CollectChangeNotifications",
            changeReader,
            null,
            changedKeys,
            CancellationToken.None));

        var overviewReader = new SequenceAsyncEnumerator<WorkChange>();
        Assert.Null(await InvokeWithResult(
            broadcaster,
            "CollectWorkerOverviewChangeNotifications",
            overviewReader,
            null,
            CancellationToken.None));
    }

    [Fact]
    public async Task BoundFullBatchesAndSkipShutdownBroadcastsWithoutSubscribers()
    {
        var broadcaster = CreateBatchingBroadcaster(maxBatchSize: 3);
        broadcaster.Dispose();

        var first = CreateEvent("first");
        var second = CreateEvent("second");
        var third = CreateEvent("third");
        var eventBatch = await InvokeWithResult(
            broadcaster,
            "CollectEventBatch",
            EventSubscription(),
            new SequenceAsyncEnumerator<WorkEvent>(second, third),
            null,
            first,
            CancellationToken.None);
        Assert.Equal(
            [first, second, third],
            Assert.IsAssignableFrom<IReadOnlyList<WorkEvent>>(
                eventBatch.GetType().GetProperty("Events")!.GetValue(eventBatch)));

        var changeReader = new SequenceAsyncEnumerator<WorkChange>(
            new(1, DateTimeOffset.UtcNow, WorkChangeKey.Definition("one")),
            new(2, DateTimeOffset.UtcNow, WorkChangeKey.Definition("two")));
        var changedKeys = new HashSet<WorkChangeKey> { WorkChangeKey.System() };
        await InvokeWithResult(
            broadcaster,
            "CollectChangeNotifications",
            changeReader,
            null,
            changedKeys,
            CancellationToken.None);
        Assert.Equal(3, changedKeys.Count);

        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var noSubscribers = CreateBroadcaster(
            provider,
            new WorkableRealtimeViewSubscriptions(),
            new RecordingHubClients("never-fails"),
            new ManualTimerFactory(),
            new RecordingLogger<WorkableRealtimeBroadcaster>());
        await noSubscribers.SystemStopping(system, WorkOrigin.Create(WorkInvocationChannel.InProcess));
    }

    [Fact]
    public async Task PreservePendingReadsWhenBatchingIsDisabled()
    {
        var broadcaster = CreateBatchingBroadcaster(maxBatchSize: 1);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var eventSubscription = EventSubscription();
        var eventBatch = await InvokeWithResult(
            broadcaster,
            "CollectEventBatch",
            eventSubscription,
            new SequenceAsyncEnumerator<WorkEvent>(),
            pending,
            CreateEvent("single"),
            CancellationToken.None);

        Assert.Same(pending, eventBatch.GetType().GetProperty("PendingRead")!.GetValue(eventBatch));
        Assert.Same(pending, await InvokeWithResult(
            broadcaster,
            "CollectChangeNotifications",
            new SequenceAsyncEnumerator<WorkChange>(),
            pending,
            new HashSet<WorkChangeKey> { WorkChangeKey.System() },
            CancellationToken.None));
        Assert.Same(pending, await InvokeWithResult(
            broadcaster,
            "CollectWorkerOverviewChangeNotifications",
            new SequenceAsyncEnumerator<WorkChange>(),
            pending,
            CancellationToken.None));
    }

    [Fact]
    public async Task StopEventPumpsAcrossCancellationDisposalRacesAndUnexpectedFaults()
    {
        var logger = new RecordingLogger();
        await StopEventPump(Task.FromCanceled(new CancellationToken(canceled: true)), logger);
        await StopEventPump(Task.FromException(new NotSupportedException("iterator disposal raced")), logger);
        await StopEventPump(Task.FromException(new InvalidOperationException("pump failed")), logger);

        var error = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains("pump scope", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task ReportWorkerOverviewDeliveryFailuresAndReleaseStreamDiagnostics()
    {
        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var handle = await system.Queue.Enqueue("signalr.broadcaster.tests");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected an accepted worker.");
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        using var watchCancellation = new CancellationTokenSource();
        var watch = subscriptions.Watch(
            "failed-connection",
            groups,
            system,
            "worker-overview",
            workerId,
            new WorkWorkerOverviewRealtimeCriteria(),
            Authorization(),
            watchCancellation.Token);
        var subscription = await TestEventually.UntilNotNull(
            () => Task.FromResult(subscriptions.GetActiveSubscriptions(system).SingleOrDefault()),
            "Expected the worker overview subscription to become active.");
        subscriptions.SetStreaming(subscription.GroupName, isStreaming: true);
        await watch;
        subscriptions.SetSeeded(subscription.GroupName, hasPublishedState: true);
        subscriptions.SetStreaming(subscription.GroupName, isStreaming: false);
        var clients = new RecordingHubClients("failed-connection");
        var logger = new RecordingLogger<WorkableRealtimeBroadcaster>();
        var broadcaster = CreateBroadcaster(
            provider,
            new WorkableRealtimeViewSubscriptions(),
            clients,
            new ManualTimerFactory(),
            logger,
            subscriptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            broadcaster,
            "BroadcastWorkerOverviewGroup",
            system,
            subscription,
            CancellationToken.None));

        Assert.Equal("Client delivery failed.", exception.Message);
        Assert.Equal(1, clients.For("failed-connection").Attempts);
        var error = Assert.Single(logger.Entries);
        Assert.Contains(workerId.Value.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));
        Assert.False(snapshot.IsStreaming);
        Assert.Equal("Client delivery failed.", snapshot.LastError);
        Assert.Null(snapshot.ChangeStreamDiagnostics);
    }

    [Fact]
    public async Task FinishAndDisposeTheViewLaneWhenItsChangeSourceCompletes()
    {
        await using var provider = CreateProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var changes = new CompletedChangeStream();
        var broadcaster = CreateBroadcaster(
            provider,
            new WorkableRealtimeViewSubscriptions(),
            new RecordingHubClients("never-fails"),
            new ManualTimerFactory(),
            new RecordingLogger<WorkableRealtimeBroadcaster>());

        await InvokeAsync(
            broadcaster,
            "BroadcastViewsFromChanges",
            system,
            changes,
            CancellationToken.None);

        Assert.True(changes.Subscription.ReaderDisposed);
        Assert.True(changes.Subscription.Disposed);
    }

    private static ServiceProvider CreateProvider()
        => new ServiceCollection()
            .AddLogging()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization(false);
                builder.UseCapacity(new WorkSystemCapacityConfiguration
                {
                    MaximumWorkers = 1,
                });
                builder.AddWork(
                    WorkDefinition.Create(
                        "signalr.broadcaster.tests",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            })
            .BuildServiceProvider();

    private static ServiceProvider CreateAuthorizedProvider()
        => new ServiceCollection()
            .AddLogging()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization(true);
                builder.AddWork(
                    WorkDefinition.Create(
                        "signalr.broadcaster.private",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization.AllowReadToGroups("private.read"));
            })
            .BuildServiceProvider();

    private static WorkableRealtimeBroadcaster CreateBroadcaster(
        ServiceProvider provider,
        WorkableRealtimeViewSubscriptions subscriptions,
        RecordingHubClients clients,
        IWorkableRealtimeTimerFactory timerFactory,
        ILogger<WorkableRealtimeBroadcaster> logger,
        WorkableRealtimeWorkerOverviewSubscriptions? workerOverviewSubscriptions = null,
        WorkableRealtimeEventSubscriptions? eventSubscriptions = null)
        => new(
            provider.GetRequiredService<IWorkSystemRegistry>(),
            new RecordingHubContext(clients),
            logger,
            new WorkableViewQueryAdapter(),
            eventSubscriptions ?? new WorkableRealtimeEventSubscriptions(),
            subscriptions,
            workerOverviewSubscriptions ?? new WorkableRealtimeWorkerOverviewSubscriptions(),
            new TestHostApplicationLifetime(),
            Options.Create(new WorkableSignalROptions
            {
                DiagnosticsPublishInterval = TimeSpan.FromSeconds(1),
            }),
            new WorkableSignalRJsonPayloadSerializer(Options.Create(new JsonHubProtocolOptions())),
            timerFactory,
            new WorkableRealtimeBroadcastLaneRunner(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkableRealtimeBroadcastLaneRunner>.Instance),
            new WorkableSignalRRegistration());

    private static WorkableRealtimeBroadcaster CreateBatchingBroadcaster(int maxBatchSize)
        => new(
            null!,
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkableRealtimeBroadcaster>.Instance,
            null!,
            null!,
            null!,
            null!,
            new TestHostApplicationLifetime(),
            Options.Create(new WorkableSignalROptions
            {
                EventMaxBatchSize = maxBatchSize,
                BatchTimeWindow = TimeSpan.FromMilliseconds(1),
                LiveTimeWindow = TimeSpan.FromMilliseconds(1),
                MinimumTimeWindow = TimeSpan.FromMilliseconds(1),
            }),
            new WorkableSignalRJsonPayloadSerializer(Options.Create(new JsonHubProtocolOptions())),
            null!,
            null!,
            new WorkableSignalRRegistration());

    private static async Task InvokeAsync(object target, string methodName, params object?[] arguments)
        => await InvokeTask(target, methodName, arguments);

    private static Task InvokeTask(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(target, arguments));
    }

    private static T InvokeStatic<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(WorkableRealtimeBroadcaster).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length);
        return Assert.IsType<T>(method.Invoke(null, arguments));
    }

    private static WorkableRealtimeViewSubscription ViewSubscription(WorkViewCriteria criteria)
        => new(
            "connection",
            "subscription",
            WorkSystemId.New(),
            "diagnostics",
            criteria,
            "group",
            0,
            0,
            Authorization());

    private static async Task<object> InvokeWithResult(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(target, arguments));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task StopEventPump(Task pumpTask, ILogger logger)
    {
        var broadcasterType = typeof(WorkableRealtimeBroadcaster);
        var pumpType = broadcasterType.GetNestedType(
            "EventPump",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pumpType);
        using var cancellation = new CancellationTokenSource();
        var pump = Activator.CreateInstance(pumpType, cancellation, pumpTask);
        Assert.NotNull(pump);
        var method = broadcasterType.GetMethod(
            "StopEventPump",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        await Assert.IsAssignableFrom<Task>(method.Invoke(null, [pump, logger, "pump scope"]));
    }

    private static WorkAuthorizationSnapshot Authorization()
        => WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            new WorkActor("realtime-broadcaster-test", "Realtime Broadcaster Test"),
            [InternalWorkAuthorizationGroups.SystemAdministrator],
            readableDefinitionIds: null);

    private static WorkAuthorizationSnapshot NoReadAuthorization()
        => WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            new WorkActor("realtime-hidden-test", "Realtime Hidden Test"),
            groups: [],
            readableDefinitionIds: [],
            isAuthenticated: true);

    private static WorkEvent CreateEvent(string eventType)
        => new(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            null,
            null,
            null,
            null,
            null,
            null,
            new HashSet<WorkIdentifier>(),
            eventType,
            null);

    private static WorkableRealtimeEventSubscriptions.EventSubscription EventSubscription()
        => new(
            "batching-connection",
            new WorkSystemId(Guid.NewGuid()),
            "batching-group",
            null,
            Authorization());

    private sealed class SequenceAsyncEnumerator<T>(params T[] items) : IAsyncEnumerator<T>
    {
        private int index = -1;

        public T Current { get; private set; } = default!;

        public ValueTask<bool> MoveNextAsync()
        {
            this.index++;
            if (this.index >= items.Length)
            {
                return ValueTask.FromResult(false);
            }

            this.Current = items[this.index];
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CompletedChangeStream : IWorkChangeStream
    {
        public CompletedChangeSubscription Subscription { get; } = new();

        public IWorkChangeSubscription Subscribe(WorkChangeSubscriptionOptions? options = null)
            => this.Subscription;
    }

    private sealed class CompletedChangeSubscription : IWorkChangeSubscription
    {
        public bool ReaderDisposed { get; private set; }

        public bool Disposed { get; private set; }

        public IAsyncEnumerable<WorkChange> Read(CancellationToken cancellationToken = default)
            => this.ReadCompleted(cancellationToken);

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<WorkChange> ReadCompleted(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }
            finally
            {
                this.ReaderDisposed = true;
            }
        }
    }

    private sealed class ManualTimerFactory : IWorkableRealtimeTimerFactory
    {
        public ManualTimer Timer { get; private set; } = new();

        public IWorkableRealtimeTimer Create(TimeSpan interval)
        {
            this.Timer = new ManualTimer();
            return this.Timer;
        }
    }

    private sealed class ManualTimer : IWorkableRealtimeTimer
    {
        private readonly System.Threading.Channels.Channel<bool> ticks =
            System.Threading.Channels.Channel.CreateUnbounded<bool>();
        private int waitCount;

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.waitCount);
            while (await this.ticks.Reader.WaitToReadAsync(cancellationToken))
            {
                if (this.ticks.Reader.TryRead(out var tick))
                {
                    return tick;
                }
            }

            return false;
        }

        public void Tick() => this.ticks.Writer.TryWrite(true);

        public Task WaitForWaitCount(int expected)
            => TestEventually.Until(
                () => Volatile.Read(ref this.waitCount) >= expected,
                $"Expected the realtime timer to begin wait {expected}.");

        public void Dispose() => this.ticks.Writer.TryComplete();
    }

    private sealed class RecordingHubContext(RecordingHubClients clients) : IHubContext<WorkableRealtimeHub>
    {
        public IHubClients Clients => clients;

        public IGroupManager Groups { get; } = new RecordingSignalRGroupManager();
    }

    private sealed class RecordingHubClients(string failingConnectionId) : IHubClients
    {
        private readonly Dictionary<string, RecordingClientProxy> clients = new(StringComparer.Ordinal);
        private readonly RecordingClientProxy broadcast = new();

        public IClientProxy All => this.broadcast;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this.broadcast;

        public IClientProxy Client(string connectionId) => this.For(connectionId);

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this.broadcast;

        public IClientProxy Group(string groupName) => this.broadcast;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this.broadcast;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this.broadcast;

        public IClientProxy User(string userId) => this.broadcast;

        public IClientProxy Users(IReadOnlyList<string> userIds) => this.broadcast;

        public RecordingClientProxy For(string connectionId)
        {
            if (!this.clients.TryGetValue(connectionId, out var client))
            {
                client = new RecordingClientProxy(
                    shouldFail: string.Equals(connectionId, failingConnectionId, StringComparison.Ordinal));
                this.clients[connectionId] = client;
            }

            return client;
        }
    }

    private sealed class RecordingClientProxy(bool shouldFail = false) : IClientProxy
    {
        public int Attempts { get; private set; }

        public List<ClientCall> Calls { get; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            this.Attempts++;
            if (shouldFail)
            {
                throw new InvalidOperationException("Client delivery failed.");
            }

            this.Calls.Add(new ClientCall(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed record ClientCall(string Method, object?[] Arguments);

    private sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;

    private class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => this.started.Token;

        public CancellationToken ApplicationStopping => this.stopping.Token;

        public CancellationToken ApplicationStopped => this.stopped.Token;

        public void StopApplication() => this.stopping.Cancel();
    }

    private static IWorkSystemDiagnostics CreateDiagnostics(int thresholdMultiple)
    {
        var activeCount = thresholdMultiple == 0 ? 0 : 1;
        var age = TimeSpan.FromSeconds(thresholdMultiple);
        return new FixedDiagnostics(
            new WorkSystemReadModelDiagnostics(
                thresholdMultiple,
                0,
                0,
                0,
                0,
                TimeSpan.Zero,
                null,
                null,
                null),
            new WorkSystemRetentionDiagnostics(
                0,
                0,
                0,
                null,
                age,
                0,
                false,
                null,
                TimeSpan.Zero,
                0,
                0,
                null,
                null),
            new WorkSystemConcurrencyDiagnostics(activeCount, age, 0),
            new WorkSystemDurabilityDiagnostics(
                activeCount,
                age,
                activeCount,
                age,
                null,
                null,
                0,
                0,
                0,
                0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                [],
                null,
                null,
                null,
                null));
    }

    private sealed class FixedDiagnostics(
        WorkSystemReadModelDiagnostics readModel,
        WorkSystemRetentionDiagnostics retention,
        WorkSystemConcurrencyDiagnostics concurrency,
        WorkSystemDurabilityDiagnostics durability) : IWorkSystemDiagnostics
    {
        public WorkSystemQueueDiagnostics Queue { get; } = new(0, null, null, null, null, 0, null, null);

        public WorkSystemReadModelDiagnostics ReadModel { get; } = readModel;

        public WorkSystemRetentionDiagnostics Retention { get; } = retention;

        public WorkSystemConcurrencyDiagnostics Concurrency { get; } = concurrency;

        public WorkSystemDurabilityDiagnostics Durability { get; } = durability;

        public WorkSystemIdempotencyDiagnostics Idempotency { get; } = new(0, null);
    }

    private sealed class DiagnosticsSession(IWorkSystemDiagnostics diagnostics) : IWorkSystemSession
    {
        public string? SystemName => null;

        public WorkSystemState SystemState => WorkSystemState.Started;

        public WorkSystemCapabilities Capabilities => WorkSystemCapabilities.None;

        public IWorkSystemDiagnostics Diagnostics { get; } = diagnostics;

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue => throw new NotSupportedException();

        public IWorkerOperations Workers => throw new NotSupportedException();

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();
    }
}

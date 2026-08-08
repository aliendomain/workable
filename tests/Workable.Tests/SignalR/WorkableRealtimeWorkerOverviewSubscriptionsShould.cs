using Microsoft.Extensions.DependencyInjection;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeWorkerOverviewSubscriptionsShould
{
    [Fact]
    public async Task WaitForStreamingBeforeCompletingWatch()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var workerId = WorkerId.New();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var watch = subscriptions.Watch(
            "connection-1",
            groups,
            system,
            " worker-panel ",
            workerId,
            Criteria(WorkComponentShapes.Standard),
            Authorization(),
            timeout.Token);

        var added = await groups.WaitForAdd();
        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));

        Assert.False(watch.IsCompleted);
        Assert.Equal("worker-panel", snapshot.SubscriptionId);
        Assert.Equal(workerId, snapshot.WorkerId);
        Assert.Equal(added.GroupName, snapshot.GroupName);
        Assert.Equal(1, snapshot.GroupConnectionCount);
        Assert.False(snapshot.IsStreaming);

        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        var subscription = await watch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("worker-panel", subscription.SubscriptionId);
        Assert.True(Assert.Single(subscriptions.GetDebugSubscriptions(system)).IsStreaming);
    }

    [Fact]
    public async Task ReuseExistingSubscriptionWhenGroupIsUnchanged()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var workerId = WorkerId.New();
        var criteria = Criteria(WorkComponentShapes.Standard);

        var first = await WatchAndStart(subscriptions, groups, system, "connection-1", "panel", workerId, criteria);
        var second = await subscriptions.Watch(
            "connection-1",
            groups,
            system,
            " panel ",
            workerId,
            criteria,
            Authorization(),
            CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(groups.Adds);
        Assert.Empty(groups.Removes);
        Assert.Single(subscriptions.GetDebugSubscriptions(system));
    }

    [Fact]
    public async Task ReplaceExistingGroupWhenCriteriaChanges()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var workerId = WorkerId.New();

        var first = await WatchAndStart(
            subscriptions,
            groups,
            system,
            "connection-1",
            "panel",
            workerId,
            Criteria(WorkComponentShapes.Compact));
        var secondWatch = subscriptions.Watch(
            "connection-1",
            groups,
            system,
            "panel",
            workerId,
            Criteria(WorkComponentShapes.Detailed),
            Authorization(),
            CancellationToken.None);
        var secondAdded = await groups.WaitForAdd();
        subscriptions.SetStreaming(secondAdded.GroupName, isStreaming: true);
        var second = await secondWatch.WaitAsync(TimeSpan.FromSeconds(1));

        var removed = Assert.Single(groups.Removes);
        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));

        Assert.NotEqual(first.GroupName, second.GroupName);
        Assert.Equal(first.GroupName, removed.GroupName);
        Assert.Equal(second.GroupName, snapshot.GroupName);
        Assert.Equal(2, groups.Adds.Count);
    }

    [Fact]
    public async Task ShareGroupForEquivalentSubscriptions()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var workerId = WorkerId.New();
        var criteria = Criteria(WorkComponentShapes.Standard);

        var first = await WatchAndStart(subscriptions, groups, system, "connection-1", "first", workerId, criteria);
        var second = await WatchAndStart(subscriptions, groups, system, "connection-2", "second", workerId, criteria);

        Assert.Equal(first.GroupName, second.GroupName);
        Assert.Equal(2, groups.Adds.Count);
        Assert.Single(subscriptions.GetActiveSubscriptions(system));
        Assert.Equal(2, subscriptions.GetGroupSubscriptions(first.GroupName).Count);
        Assert.All(subscriptions.GetDebugSubscriptions(system), snapshot => Assert.Equal(2, snapshot.GroupConnectionCount));
    }

    [Fact]
    public async Task RemoveMatchingGroupWhenWorkerOverviewIsUnwatched()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        var subscription = await WatchAndStart(
            subscriptions,
            groups,
            system,
            "connection-1",
            "panel",
            WorkerId.New(),
            Criteria(WorkComponentShapes.Standard));

        await subscriptions.Unwatch(
            "connection-1",
            groups,
            system,
            " panel ",
            CancellationToken.None);

        var removed = Assert.Single(groups.Removes);
        Assert.Equal(subscription.GroupName, removed.GroupName);
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
    }

    [Fact]
    public async Task RemoveEveryGroupForDisconnectedConnection()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        await WatchAndStart(subscriptions, groups, system, "connection-1", "first", WorkerId.New(), Criteria(WorkComponentShapes.Compact));
        await WatchAndStart(subscriptions, groups, system, "connection-1", "second", WorkerId.New(), Criteria(WorkComponentShapes.Compact));
        await WatchAndStart(subscriptions, groups, system, "connection-2", "third", WorkerId.New(), Criteria(WorkComponentShapes.Compact));

        await subscriptions.RemoveConnection("connection-1", groups, CancellationToken.None);

        Assert.Equal(2, groups.Removes.Count);
        Assert.All(groups.Removes, remove => Assert.Equal("connection-1", remove.ConnectionId));
        Assert.Collection(
            subscriptions.GetDebugSubscriptions(system),
            snapshot => Assert.Equal("connection-2", snapshot.ConnectionId));
    }

    [Fact]
    public async Task ExposeStreamingActivityErrorAndEventDiagnostics()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var subscription = await WatchAndStart(
            subscriptions,
            groups,
            system,
            "connection-1",
            "panel",
            WorkerId.New(),
            Criteria(WorkComponentShapes.Standard));
        var occurredAt = DateTimeOffset.UtcNow;
        var diagnostics = new WorkChangeSubscriptionDiagnosticsSnapshot(
            Capacity: 16,
            QueuedCount: 3,
            PeakQueuedCount: 4,
            AcceptedChangeCount: 5,
            DeliveredChangeCount: 2,
            CoalescedChangeCount: 1,
            DroppedChangeCount: 1);
        var observedVersion = subscriptions.Version;
        var changed = subscriptions.WaitForChange(observedVersion, CancellationToken.None);

        subscriptions.ReportActivity(subscription.GroupName, occurredAt);
        await changed.WaitAsync(TimeSpan.FromSeconds(1));
        subscriptions.ReportError(subscription.GroupName, "  stream stopped  ");
        subscriptions.SetChangeStreamDiagnosticsProvider(subscription.GroupName, () => diagnostics);

        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));
        Assert.Equal(occurredAt, snapshot.LastActivityAt);
        Assert.Equal("stream stopped", snapshot.LastError);
        Assert.NotNull(snapshot.StreamingStoppedAt);
        Assert.Equal(diagnostics, snapshot.ChangeStreamDiagnostics);
    }

    [Fact]
    public async Task RollBackSubscriptionWhenGroupAddFails()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager { FailAdds = true };
        await using var system = CreateSystem();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subscriptions.Watch(
            "connection-1",
            groups,
            system,
            "panel",
            WorkerId.New(),
            Criteria(WorkComponentShapes.Standard),
            Authorization(),
            CancellationToken.None));

        Assert.Equal("Add failed.", exception.Message);
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
        Assert.Empty(groups.Removes);
    }

    private static async Task<WorkableRealtimeWorkerOverviewSubscription> WatchAndStart(
        WorkableRealtimeWorkerOverviewSubscriptions subscriptions,
        RecordingSignalRGroupManager groups,
        IWorkSystem system,
        string connectionId,
        string subscriptionId,
        WorkerId workerId,
        WorkWorkerOverviewRealtimeCriteria criteria)
    {
        var watch = subscriptions.Watch(
            connectionId,
            groups,
            system,
            subscriptionId,
            workerId,
            criteria,
            Authorization(),
            CancellationToken.None);
        var added = await groups.WaitForAdd();
        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        return await watch.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static WorkWorkerOverviewRealtimeCriteria Criteria(string workerTimeline)
        => new(
            WorkerControls: WorkComponentShapes.Standard,
            WorkerLogs: WorkComponentShapes.Compact,
            WorkerDuration: WorkComponentShapes.Standard,
            WorkerTimeline: workerTimeline);

    private static IWorkSystem CreateSystem()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            WorkDefinition.Create("signalr.worker-overview.subscription"),
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success())));
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private static WorkAuthorizationSnapshot Authorization()
        => WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            new WorkActor("signalr-worker-overview-subscription-user", "SignalR Worker Overview Subscription User"),
            ["signalr.read"],
            readableDefinitionIds: null);
}

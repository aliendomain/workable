using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeWorkerOverviewSubscriptionsShould
{
    [Fact]
    public async Task EnforcePerConnectionAndGlobalWorkerOverviewSubscriptionLimits()
    {
        await using var system = CreateSystem();
        var groups = new RecordingSignalRGroupManager();
        var criteria = Criteria(WorkComponentShapes.Standard);
        var perConnection = new WorkableRealtimeWorkerOverviewSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 1,
            MaximumSubscriptionsPerKind = 2,
        }));
        await WatchAndStart(perConnection, groups, system, "connection-1", "first", WorkerId.New(), criteria);

        await Assert.ThrowsAsync<HubException>(() => perConnection.Watch(
            "connection-1", groups, system, "second", WorkerId.New(), criteria,
            Authorization(), CancellationToken.None));

        var global = new WorkableRealtimeWorkerOverviewSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 1,
            MaximumSubscriptionsPerKind = 1,
        }));
        await WatchAndStart(global, groups, system, "connection-1", "first", WorkerId.New(), criteria);

        await Assert.ThrowsAsync<HubException>(() => global.Watch(
            "connection-2", groups, system, "second", WorkerId.New(), criteria,
            Authorization(), CancellationToken.None));
    }

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
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

        Assert.False(watch.IsCompleted);
        Assert.Equal("worker-panel", snapshot.SubscriptionId);
        Assert.Equal(workerId, snapshot.WorkerId);
        Assert.Equal(added.GroupName, snapshot.GroupName);
        Assert.Equal(1, snapshot.GroupConnectionCount);
        Assert.False(snapshot.IsStreaming);

        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        var subscription = await watch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("worker-panel", subscription.SubscriptionId);
        Assert.True(Assert.Single(subscriptions.GetSubscriptionSnapshots(system)).IsStreaming);
    }

    [Fact]
    public async Task ReleaseAWatcherThatLosesItsGroupBeforeStreamingStarts()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var watch = subscriptions.Watch(
            "connection",
            groups,
            system,
            "panel",
            WorkerId.New(),
            Criteria(WorkComponentShapes.Standard),
            Authorization(),
            CancellationToken.None);
        await groups.WaitForAdd();

        subscriptions.RemoveConnection("connection");

        await watch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
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
        Assert.Single(subscriptions.GetSubscriptionSnapshots(system));
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
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

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
        Assert.All(subscriptions.GetSubscriptionSnapshots(system), snapshot => Assert.Equal(2, snapshot.GroupConnectionCount));
    }

    [Fact]
    public async Task SeparateGroupsWhenActorsDifferDespiteEquivalentReadScope()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var workerId = WorkerId.New();
        var criteria = Criteria(WorkComponentShapes.Standard);

        var first = await WatchAndStart(
            subscriptions, groups, system, "connection-1", "first", workerId, criteria,
            Authorization("actor-1"));
        var second = await WatchAndStart(
            subscriptions, groups, system, "connection-2", "second", workerId, criteria,
            Authorization("actor-2"));

        Assert.NotEqual(first.GroupName, second.GroupName);
        Assert.Equal(2, subscriptions.GetActiveSubscriptions(system).Count);
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
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
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

        subscriptions.RemoveConnection("connection-1");

        Assert.Empty(groups.Removes);
        Assert.Collection(
            subscriptions.GetSubscriptionSnapshots(system),
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

        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));
        Assert.Equal(occurredAt, snapshot.LastActivityAt);
        Assert.Equal("stream stopped", snapshot.LastError);
        Assert.NotNull(snapshot.StreamingStoppedAt);
        Assert.Equal(diagnostics, snapshot.ChangeStreamDiagnostics);
    }

    [Fact]
    public async Task IgnoreMissingGroupsAndTrackEverySeedStateTransition()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        const string Missing = "missing-group";

        await subscriptions.WaitForChange(subscriptions.Version - 1, CancellationToken.None);
        Assert.False(subscriptions.IsSeeded(Missing));
        Assert.False(subscriptions.HasPublishedState(Missing));
        subscriptions.SetSeeded(Missing, hasPublishedState: true);
        subscriptions.ReportActivity(Missing, DateTimeOffset.UtcNow);
        subscriptions.ReportError(Missing, "ignored");
        subscriptions.SetChangeStreamDiagnosticsProvider(Missing, diagnosticsProvider: null);

        var subscription = await WatchAndStart(
            subscriptions,
            groups,
            system,
            "connection-seed",
            "panel",
            WorkerId.New(),
            Criteria(WorkComponentShapes.Standard));
        Assert.False(subscriptions.IsSeeded(subscription.GroupName));
        Assert.False(subscriptions.HasPublishedState(subscription.GroupName));

        subscriptions.SetSeeded(subscription.GroupName, hasPublishedState: false);
        Assert.True(subscriptions.IsSeeded(subscription.GroupName));
        Assert.False(subscriptions.HasPublishedState(subscription.GroupName));
        subscriptions.SetSeeded(subscription.GroupName, hasPublishedState: true);
        Assert.True(subscriptions.HasPublishedState(subscription.GroupName));
        subscriptions.SetSeeded(subscription.GroupName, hasPublishedState: true);
        subscriptions.SetSeeded(subscription.GroupName, hasPublishedState: false);
        subscriptions.ReportError(subscription.GroupName, " ");
        Assert.Null(Assert.Single(subscriptions.GetSubscriptionSnapshots(system)).LastError);
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
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
        Assert.Empty(groups.Removes);
    }

    [Fact]
    public async Task KeepNewerSubscriptionWhenPreviousGroupAddFailsAfterReplacement()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new DelayedFirstAddGroupManager();
        await using var system = CreateSystem();

        var first = subscriptions.Watch(
            "connection-1", groups, system, "panel", WorkerId.New(),
            Criteria(WorkComponentShapes.Compact), Authorization(), CancellationToken.None);
        await groups.WaitForFirstAdd();

        var secondWatch = subscriptions.Watch(
            "connection-1", groups, system, "panel", WorkerId.New(),
            Criteria(WorkComponentShapes.Detailed), Authorization(), CancellationToken.None);
        var secondAdd = await groups.WaitForSecondAdd();
        subscriptions.SetStreaming(secondAdd.GroupName, isStreaming: true);
        var second = await secondWatch;

        groups.FailFirstAdd();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

        Assert.Equal("First add failed.", exception.Message);
        Assert.Equal(second.GroupName, snapshot.GroupName);
        Assert.Equal(second.GroupName, Assert.Single(subscriptions.GetActiveSubscriptions(system)).GroupName);
    }

    [Fact]
    public async Task FilterSnapshotsForOtherSystemsAndIgnoreMissingReleaseGroups()
    {
        var subscriptions = new WorkableRealtimeWorkerOverviewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        await using var other = CreateSystem();

        await WatchAndStart(
            subscriptions,
            groups,
            system,
            "connection-1",
            "panel",
            WorkerId.New(),
            Criteria(WorkComponentShapes.Standard));

        Assert.Empty(subscriptions.GetActiveSubscriptions(other));
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(other));
        typeof(WorkableRealtimeWorkerOverviewSubscriptions)
            .GetMethod("ReleaseGroupLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(subscriptions, ["missing-group"]);
    }

    private static async Task<WorkableRealtimeWorkerOverviewSubscription> WatchAndStart(
        WorkableRealtimeWorkerOverviewSubscriptions subscriptions,
        RecordingSignalRGroupManager groups,
        IWorkSystem system,
        string connectionId,
        string subscriptionId,
        WorkerId workerId,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkAuthorizationSnapshot? authorization = null)
    {
        var watch = subscriptions.Watch(
            connectionId,
            groups,
            system,
            subscriptionId,
            workerId,
            criteria,
            authorization ?? Authorization(),
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

    private static WorkAuthorizationSnapshot Authorization(
        string actorId = "signalr-worker-overview-subscription-user")
        => WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            new WorkActor(actorId, "SignalR Worker Overview Subscription User"),
            ["signalr.read"],
            readableDefinitionIds: null);

    private sealed class DelayedFirstAddGroupManager : IGroupManager
    {
        private readonly TaskCompletionSource<object?> firstAddStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> firstAddResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SignalRGroupCall> secondAdd = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int addCount;

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this.addCount) == 1)
            {
                this.firstAddStarted.SetResult(null);
                return this.firstAddResult.Task;
            }

            this.secondAdd.SetResult(new SignalRGroupCall(connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WaitForFirstAdd()
            => this.firstAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public Task<SignalRGroupCall> WaitForSecondAdd()
            => this.secondAdd.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public void FailFirstAdd()
            => this.firstAddResult.SetException(new InvalidOperationException("First add failed."));
    }
}

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeViewSubscriptionsShould
{
    [Fact]
    public async Task EnforcePerConnectionAndGlobalNamedViewSubscriptionLimits()
    {
        await using var system = CreateSystem();
        var groups = new RecordingSignalRGroupManager();
        var criteria = Criteria("workers", WorkComponentShapes.Compact);
        var perConnection = new WorkableRealtimeViewSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 1,
            MaximumSubscriptionsPerKind = 2,
        }));
        await perConnection.WatchView(
            "connection-1", groups, system, "first", "overview", criteria, Authorization(), CancellationToken.None);

        await Assert.ThrowsAsync<HubException>(() => perConnection.WatchView(
            "connection-1", groups, system, "second", "overview", criteria, Authorization(), CancellationToken.None));

        var global = new WorkableRealtimeViewSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 1,
            MaximumSubscriptionsPerKind = 1,
        }));
        await global.WatchView(
            "connection-1", groups, system, "first", "overview", criteria, Authorization(), CancellationToken.None);

        await Assert.ThrowsAsync<HubException>(() => global.WatchView(
            "connection-2", groups, system, "second", "overview", criteria, Authorization(), CancellationToken.None));
    }

    [Fact]
    public async Task SeparateGroupsWhenWorkflowProjectionAuthorizationDiffers()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = Criteria("workflowRuns", WorkComponentShapes.Standard);
        var readableWorkflow = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "workflow-runs-readable",
            "workflow-runs",
            criteria,
            Authorization([WorkflowDefinitionId.New()]),
            CancellationToken.None);
        var hiddenWorkflow = await subscriptions.WatchView(
            "connection-2",
            groups,
            system,
            "workflow-runs-hidden",
            "workflow-runs",
            criteria,
            Authorization(),
            CancellationToken.None);

        Assert.NotEqual(readableWorkflow.GroupName, hiddenWorkflow.GroupName);
    }

    [Fact]
    public async Task WaitForTheSharedChangeStreamBeforeSeedingAView()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        await using var system = CreateSystem();

        var waiting = subscriptions.WaitForStreaming(system, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        Assert.False(subscriptions.SetStreaming(system, isStreaming: true));
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(subscriptions.SetStreaming(system, isStreaming: false));
        var waitingForRestart = subscriptions.WaitForStreaming(system, CancellationToken.None);
        Assert.False(waitingForRestart.IsCompleted);

        Assert.True(subscriptions.SetStreaming(system, isStreaming: true));
        await waitingForRestart.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KeepAGroupBehindItsSeedBarrierUntilEveryJoiningConnectionIsSeeded()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = Criteria("workers", WorkComponentShapes.Compact);
        var first = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "first",
            "overview",
            criteria,
            Authorization(),
            CancellationToken.None);
        var second = await subscriptions.WatchView(
            "connection-2",
            groups,
            system,
            "second",
            "overview",
            criteria,
            Authorization(),
            CancellationToken.None);
        var waiting = subscriptions.WaitForSeed(first.GroupName, CancellationToken.None);

        Assert.Equal(first.GroupName, second.GroupName);
        Assert.False(waiting.IsCompleted);

        subscriptions.CompleteSeed(first.GroupName);
        Assert.False(waiting.IsCompleted);

        subscriptions.CompleteSeed(second.GroupName);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TreatMissingAndAlreadyCompletedSeedGroupsAsNonBlockingBoundaries()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        Assert.True(subscriptions.DeferBroadcastUntilSeeded("missing-group"));
        subscriptions.CompleteSeed("missing-group");

        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var subscription = await subscriptions.WatchView(
            "connection",
            groups,
            system,
            "panel",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);
        subscriptions.CompleteSeed(subscription.GroupName);
        subscriptions.CompleteSeed(subscription.GroupName);

        Assert.False(subscriptions.DeferBroadcastUntilSeeded(subscription.GroupName));
    }

    [Fact]
    public async Task ReleaseSeedWaitersWhenTheLastConnectionLeavesBeforeItsSeedCompletes()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var subscription = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);
        var waiting = subscriptions.WaitForSeed(subscription.GroupName, CancellationToken.None);

        await subscriptions.UnwatchView(
            "connection-1",
            groups,
            system,
            "panel",
            CancellationToken.None);

        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task NormalizeViewNameAndSubscriptionIdWhenCreatingGroups()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = Criteria("workers", WorkComponentShapes.Compact);

        var subscription = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            " panel ",
            " Overview ",
            criteria,
            Authorization(),
            CancellationToken.None);

        var added = Assert.Single(groups.Adds);
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

        Assert.Equal("panel", subscription.SubscriptionId);
        Assert.Equal("overview", subscription.ViewName);
        Assert.Equal(criteria, subscription.Criteria);
        Assert.Equal(added.GroupName, subscription.GroupName);
        Assert.Equal("connection-1", snapshot.ConnectionId);
        Assert.Equal("panel", snapshot.SubscriptionId);
        Assert.Equal("overview", snapshot.ViewName);
        Assert.Equal(1, snapshot.GroupConnectionCount);
        Assert.Equal(subscription.InitialReadModelSequence, snapshot.InitialReadModelSequence);
    }

    [Fact]
    public async Task ReuseExistingSubscriptionWhenGroupIsUnchanged()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = Criteria("workers", WorkComponentShapes.Compact);

        var first = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            criteria,
            Authorization(),
            CancellationToken.None);
        var second = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            " panel ",
            " Overview ",
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
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        var first = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);
        var second = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            Criteria("workers", WorkComponentShapes.Detailed),
            Authorization(),
            CancellationToken.None);

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
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = Criteria("workers", WorkComponentShapes.Compact);

        var first = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "first",
            "overview",
            criteria,
            Authorization(),
            CancellationToken.None);
        var second = await subscriptions.WatchView(
            "connection-2",
            groups,
            system,
            "second",
            "overview",
            criteria,
            Authorization(),
            CancellationToken.None);

        Assert.Equal(first.GroupName, second.GroupName);
        Assert.Equal(2, groups.Adds.Count);
        Assert.Single(subscriptions.GetActiveSubscriptions(system));
        Assert.Equal(2, subscriptions.GetGroupSubscriptions(first.GroupName).Count);
        Assert.All(subscriptions.GetSubscriptionSnapshots(system), snapshot => Assert.Equal(2, snapshot.GroupConnectionCount));
    }

    [Fact]
    public async Task SeedReconciliationIgnoresMissingAndOtherSystemGroups()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var firstSystem = CreateSystem();
        await using var secondSystem = CreateSystem("second");
        var first = await Watch(subscriptions, groups, firstSystem, "first", "workers", WorkComponentShapes.Compact);
        var second = await subscriptions.WatchView(
            "second",
            groups,
            secondSystem,
            "workers",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(systemName: "second"),
            CancellationToken.None);
        Assert.True(subscriptions.DeferBroadcastUntilSeeded(first.GroupName));
        Assert.True(subscriptions.DeferBroadcastUntilSeeded(second.GroupName));
        subscriptions.CompleteSeed(first.GroupName);
        subscriptions.CompleteSeed(second.GroupName);
        var readyGroups = Assert.IsType<HashSet<string>>(typeof(WorkableRealtimeViewSubscriptions)
            .GetField("groupsReadyForReconciliation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(subscriptions));
        readyGroups.Add("missing-group");

        var firstReady = await subscriptions.WaitForSeedReconciliations(firstSystem, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        var secondReady = await subscriptions.WaitForSeedReconciliations(secondSystem, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(first.GroupName, Assert.Single(firstReady).GroupName);
        Assert.Equal(second.GroupName, Assert.Single(secondReady).GroupName);
    }

    [Fact]
    public async Task SeparateGroupsWhenActorsDifferDespiteEquivalentReadScope()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = Criteria("workers", WorkComponentShapes.Compact);

        var first = await subscriptions.WatchView(
            "connection-1", groups, system, "first", "overview", criteria,
            Authorization(actorId: "actor-1"), CancellationToken.None);
        var second = await subscriptions.WatchView(
            "connection-2", groups, system, "second", "overview", criteria,
            Authorization(actorId: "actor-2"), CancellationToken.None);

        Assert.NotEqual(first.GroupName, second.GroupName);
        Assert.Equal(2, subscriptions.GetActiveSubscriptions(system).Count);
    }

    [Fact]
    public async Task RemoveMatchingGroupWhenViewIsUnwatched()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        var subscription = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            " panel ",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);

        await subscriptions.UnwatchView(
            "connection-1",
            groups,
            system,
            "panel",
            CancellationToken.None);

        var removed = Assert.Single(groups.Removes);
        Assert.Equal(subscription.GroupName, removed.GroupName);
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
    }

    [Fact]
    public async Task RemoveEveryGroupForDisconnectedConnection()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        await Watch(subscriptions, groups, system, "connection-1", "workers", WorkComponentShapes.Compact);
        await Watch(subscriptions, groups, system, "connection-1", "logs", WorkComponentShapes.Compact);
        await Watch(subscriptions, groups, system, "connection-2", "workers", WorkComponentShapes.Compact);

        subscriptions.RemoveConnection("connection-1");

        Assert.Empty(groups.Removes);
        Assert.Collection(
            subscriptions.GetSubscriptionSnapshots(system),
            snapshot => Assert.Equal("connection-2", snapshot.ConnectionId));
    }

    [Fact]
    public async Task RollBackSubscriptionWhenGroupAddFails()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager { FailAdds = true };
        await using var system = CreateSystem();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
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
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new DelayedFirstAddGroupManager();
        await using var system = CreateSystem();

        var first = subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);
        await groups.WaitForFirstAdd();

        var second = await subscriptions.WatchView(
            "connection-1",
            groups,
            system,
            "panel",
            "overview",
            Criteria("logs", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);

        groups.FailFirstAdd();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

        Assert.Equal("First add failed.", exception.Message);
        Assert.Equal(second.GroupName, snapshot.GroupName);
        Assert.Equal(second.GroupName, Assert.Single(groups.Adds).GroupName);
        Assert.Equal(second.GroupName, Assert.Single(subscriptions.GetActiveSubscriptions(system)).GroupName);
    }

    [Fact]
    public async Task FilterOtherSystemsAndSupportCustomSystemsWithoutProjectionClocks()
    {
        var subscriptions = new WorkableRealtimeViewSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var inner = CreateSystem();
        await using var other = CreateSystem();
        var custom = new ClocklessWorkSystem(inner);

        var ordinary = await subscriptions.WatchView(
            "connection-1",
            groups,
            custom,
            "ordinary",
            "overview",
            Criteria("workers", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);
        var workflow = await subscriptions.WatchView(
            "connection-2",
            groups,
            custom,
            "workflow",
            "workflow-runs",
            Criteria("workflowRuns", WorkComponentShapes.Compact),
            Authorization(),
            CancellationToken.None);

        Assert.Equal(0, ordinary.InitialReadModelSequence);
        Assert.Equal(0, ordinary.InitialWorkflowSequence);
        Assert.Equal(0, workflow.InitialWorkflowSequence);
        Assert.Empty(subscriptions.GetActiveSubscriptions(other));
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(other));
        Assert.False(subscriptions.SetStreaming(custom, isStreaming: true));
        Assert.False(subscriptions.SetStreaming(custom, isStreaming: true));

        typeof(WorkableRealtimeViewSubscriptions)
            .GetMethod("ReleaseGroupLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(subscriptions, ["missing-group"]);
    }

    private static Task<WorkableRealtimeViewSubscription> Watch(
        WorkableRealtimeViewSubscriptions subscriptions,
        RecordingSignalRGroupManager groups,
        IWorkSystem system,
        string connectionId,
        string componentId,
        string shape)
        => subscriptions.WatchView(
            connectionId,
            groups,
            system,
            componentId,
            "overview",
            Criteria(componentId, shape),
            Authorization(),
            CancellationToken.None);

    private static WorkViewCriteria Criteria(string componentId, string shape)
        => new(Components:
        [
            new WorkComponentRequest(
                componentId,
                componentId,
                JsonSerializer.SerializeToElement(new { marker = componentId }),
                shape),
        ]);

    private static IWorkSystem CreateSystem(string? name = null)
    {
        var services = new ServiceCollection();
        void Configure(IWorkSystemBuilder builder) => builder.AddWork(
                WorkDefinition.Create("signalr.view.subscription"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        if (name is null)
        {
            services.AddWorkableSystem(Configure);
        }
        else
        {
            services.AddWorkableSystem(name, Configure);
        }
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private static WorkAuthorizationSnapshot Authorization(
        IReadOnlyList<WorkflowDefinitionId>? readableWorkflowDefinitionIds = null,
        string actorId = "signalr-view-subscription-user",
        string? systemName = null)
        => WorkAuthorizationSnapshot.CreateForSystem(
            systemName,
            new WorkActor(actorId, "SignalR View Subscription User"),
            ["signalr.read"],
            readableDefinitionIds: null,
            readableWorkflowDefinitionIds);

    private sealed class DelayedFirstAddGroupManager : IGroupManager
    {
        private readonly TaskCompletionSource<object?> firstAddStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> firstAddResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int addCount;

        public List<SignalRGroupCall> Adds { get; } = [];

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            var call = new SignalRGroupCall(connectionId, groupName);
            if (Interlocked.Increment(ref this.addCount) == 1)
            {
                this.firstAddStarted.SetResult(null);
                return this.firstAddResult.Task;
            }

            this.Adds.Add(call);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WaitForFirstAdd()
            => this.firstAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public void FailFirstAdd()
            => this.firstAddResult.SetException(new InvalidOperationException("First add failed."));
    }

    private sealed class ClocklessWorkSystem(IWorkSystem inner) : IWorkSystem
    {
        public WorkSystemId Id => inner.Id;

        public string? Name => inner.Name;

        public bool RequiresAuthorization => inner.RequiresAuthorization;

        public WorkSystemState State => inner.State;

        public IWorkCatalog Catalog => inner.Catalog;

        public IWorkQueueService Queue => inner.Queue;

        public IWorkerOperations Workers => inner.Workers;

        public IWorkQueryService Query => inner.Query;

        public IWorkEventStream Events => inner.Events;

        public IWorkIterationStatusStream IterationStatuses => inner.IterationStatuses;

        public IWorkChangeStream Changes => inner.Changes;

        public IWorkSystemDiagnostics Diagnostics => inner.Diagnostics;

        public ValueTask<WorkSystemAccessSummary> DescribeAccess(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.DescribeAccess(requestContext, cancellationToken);

        public ValueTask<IWorkSystemSession> CreateSession(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.CreateSession(requestContext, cancellationToken);

        public Task Start(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.Start(requestContext, cancellationToken);

        public Task<WorkSystemStopResult> Stop(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.Stop(requestContext, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

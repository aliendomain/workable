using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using System.Reflection;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeEventSubscriptionsShould
{
    [Fact]
    public async Task CanonicalizeEmptyPartialAndCompleteFilterShapes()
    {
        await using var system = CreateSystem();
        var subscriptions = new WorkableRealtimeEventSubscriptions();

        Assert.Null(subscriptions.CreateFilter(system.Catalog, null));
        Assert.Null(subscriptions.CreateFilter(system.Catalog, new WorkableRealtimeEventCriteria()));
        Assert.NotNull(subscriptions.CreateFilter(
            system.Catalog,
            new WorkableRealtimeEventCriteria(EventTypes: ["worker.completed"])));
        Assert.NotNull(subscriptions.CreateFilter(
            system.Catalog,
            new WorkableRealtimeEventCriteria(DefinitionNames: ["signalr.subscription.first"])));
        Assert.NotNull(subscriptions.CreateFilter(
            system.Catalog,
            new WorkableRealtimeEventCriteria(Keys: [new(null, "tenant", "one")])));

        Assert.Equal("system", FilterKey(null));
        Assert.Equal("system", FilterKey(new WorkEventFilter()));
        var complete = new WorkEventFilter(
            WorkerId.New(),
            "signalr.subscription.first",
            new HashSet<string> { "signalr.subscription.second" },
            new WorkSubjectId("subject", "one"),
            new WorkConcurrencyKey("tenant", "two"),
            new WorkIdentifier("order", "three"),
            new HashSet<WorkEventKeyFilter>
            {
                new(null, "any", "four"),
                new(WorkKeyKind.Identifier, "order", "three"),
            },
            "worker.completed",
            new HashSet<string> { "worker.failed" })
        {
            DefinitionKind = WorkEventDefinitionKind.Work,
        };
        var completeKey = FilterKey(complete);
        Assert.Equal(64, completeKey.Length);
        Assert.NotEqual("system", completeKey);

        var ordered = subscriptions.CreateFilter(
            system.Catalog,
            new WorkableRealtimeEventCriteria(Keys:
            [
                new(WorkKeyKind.Subject, "z", "3"),
                new(null, "a", "1"),
                new(WorkKeyKind.Identifier, "m", "2"),
            ]));
        Assert.Equal(3, Required(ordered?.Keys).Count);
        Assert.Contains(Required(ordered?.Keys), key => key.Kind is null);
        Assert.Contains(Required(ordered?.Keys), key => key.Kind == WorkKeyKind.Identifier);
        Assert.Contains(Required(ordered?.Keys), key => key.Kind == WorkKeyKind.Subject);
    }

    [Fact]
    public async Task ReturnImmediatelyWhenTheObservedEventSubscriptionVersionIsStale()
    {
        await using var system = CreateSystem();
        var subscriptions = new WorkableRealtimeEventSubscriptions();

        await subscriptions.WaitForChange(subscriptions.Version - 1, CancellationToken.None);

        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
    }

    [Fact]
    public async Task ReleaseAWatcherThatLosesItsGroupBeforeStreamingStarts()
    {
        await using var system = CreateSystem();
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        var watch = subscriptions.WatchEvents(
            "connection",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(),
            CancellationToken.None);
        await groups.WaitForAdd();

        subscriptions.RemoveConnection("connection");

        await watch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
    }

    [Fact]
    public async Task CreateStableSystemEventGroupsForEveryCollectionFilterShape()
    {
        await using var system = CreateSystem();
        var method = typeof(WorkableRealtimeEventSubscriptions).GetMethod(
            "CreateSystemEventsGroupName",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string Group(WorkEventFilter? filter) => Assert.IsType<string>(method.Invoke(
            null,
            [system, filter, "read-fingerprint"]));

        var unfiltered = Group(null);
        Assert.Equal(unfiltered, Group(new WorkEventFilter()));
        Assert.NotEqual(unfiltered, Group(new WorkEventFilter(EventTypes: new HashSet<string> { "worker.completed" })));
        Assert.NotEqual(unfiltered, Group(new WorkEventFilter(DefinitionNames: new HashSet<string> { "signalr.subscription.first" })));
        Assert.NotEqual(unfiltered, Group(new WorkEventFilter(Keys: new HashSet<WorkEventKeyFilter>
        {
            new(WorkKeyKind.Subject, "invoice", "42"),
        })));
    }

    [Fact]
    public async Task EnforcePerConnectionAndGlobalRawEventSubscriptionLimits()
    {
        await using var system = CreateSystem();
        var groups = new RecordingSignalRGroupManager();
        var perConnection = new WorkableRealtimeEventSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 1,
            MaximumSubscriptionsPerKind = 2,
        }));
        await WatchAndStart(
            perConnection, groups, system, "connection-1", new WorkableRealtimeEventCriteria(["worker.queued"]));

        await Assert.ThrowsAsync<HubException>(() => perConnection.WatchEvents(
            "connection-1", groups, system, system.Catalog,
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(), CancellationToken.None));

        var global = new WorkableRealtimeEventSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 1,
            MaximumSubscriptionsPerKind = 1,
        }));
        await WatchAndStart(
            global, groups, system, "connection-1", new WorkableRealtimeEventCriteria(["worker.queued"]));

        await Assert.ThrowsAsync<HubException>(() => global.WatchEvents(
            "connection-2", groups, system, system.Catalog,
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(), CancellationToken.None));
    }

    [Fact]
    public async Task NormalizeCriteriaWhenCreatingEventGroups()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        const string definitionName = "signalr.subscription.first";
        var definitionId = DefinitionId(system, definitionName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var watch = subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(
                EventTypes: [" worker.completed ", "WORKER.COMPLETED"],
                DefinitionNames: [$" {definitionName} ", definitionName],
                Keys:
                [
                    new WorkableRealtimeEventKeyCriteria(WorkKeyKind.Identifier, " batch ", " accepted "),
                    new WorkableRealtimeEventKeyCriteria(WorkKeyKind.Identifier, "batch", "accepted"),
                ]),
            Authorization(readableDefinitionIds: [definitionId]),
            timeout.Token);

        var added = await groups.WaitForAdd();
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));
        var filter = snapshot.Filter ?? throw new InvalidOperationException("Expected filtered subscription.");
        var key = Assert.Single(Required(filter.Keys));

        Assert.False(watch.IsCompleted);
        Assert.Equal("connection-1", snapshot.ConnectionId);
        Assert.Equal(added.GroupName, snapshot.GroupName);
        Assert.Equal(1, snapshot.GroupConnectionCount);
        Assert.False(snapshot.IsStreaming);
        Assert.Equal(["worker.completed"], Required(filter.EventTypes).ToArray());
        Assert.Equal([definitionName], Required(filter.DefinitionNames).ToArray());
        Assert.Equal(WorkKeyKind.Identifier, key.Kind);
        Assert.Equal("batch", key.Type);
        Assert.Equal("accepted", key.Value);

        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        await watch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(Assert.Single(subscriptions.GetSubscriptionSnapshots(system)).IsStreaming);
    }

    [Fact]
    public async Task KeepStructurallyDistinctKeyFiltersInDistinctGroups()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var firstCriteria = new WorkableRealtimeEventCriteria(Keys:
        [
            new WorkableRealtimeEventKeyCriteria(WorkKeyKind.Identifier, "tenant:region", "west"),
        ]);
        var secondCriteria = new WorkableRealtimeEventCriteria(Keys:
        [
            new WorkableRealtimeEventKeyCriteria(WorkKeyKind.Identifier, "tenant", "region:west"),
        ]);

        var firstWatch = subscriptions.WatchEvents(
            "connection-1", groups, system, system.Catalog,
            firstCriteria, Authorization(), CancellationToken.None);
        var firstAdd = await groups.WaitForAdd();
        subscriptions.SetStreaming(firstAdd.GroupName, isStreaming: true);
        await firstWatch.WaitAsync(TimeSpan.FromSeconds(1));

        var secondWatch = subscriptions.WatchEvents(
            "connection-2", groups, system, system.Catalog,
            secondCriteria, Authorization(), CancellationToken.None);
        var secondAdd = await groups.WaitForAdd();
        subscriptions.SetStreaming(secondAdd.GroupName, isStreaming: true);
        await secondWatch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotEqual(firstAdd.GroupName, secondAdd.GroupName);
        Assert.Equal(2, subscriptions.GetActiveSubscriptions(system).Count);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task RejectMalformedCriteriaWithoutRetainingAnEventSubscription(
        bool invalidEventType,
        bool invalidDefinitionName,
        bool invalidKey,
        bool invalidKeyKind)
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = new WorkableRealtimeEventCriteria(
            EventTypes: invalidEventType ? [" "] : ["worker.completed"],
            DefinitionNames: invalidDefinitionName ? [" "] : ["signalr.subscription.first"],
            Keys: invalidKey
                ? [new WorkableRealtimeEventKeyCriteria(null, " ", "value")]
                : [new WorkableRealtimeEventKeyCriteria(
                    invalidKeyKind ? (WorkKeyKind)999 : null,
                    "batch",
                    "accepted")]);

        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            criteria,
            Authorization(),
            CancellationToken.None));

        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
        Assert.Empty(groups.Adds);
    }

    [Fact]
    public async Task RejectCriteriaThatExceedConfiguredCountOrStringLimitsBeforeRetention()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions(Options.Create(new WorkableSignalROptions
        {
            MaximumEventFilterValuesPerField = 1,
            MaximumEventFilterValueLength = 8,
        }));
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-count",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(EventTypes: ["queued", "completed"]),
            Authorization(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-length",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(Keys:
            [
                new WorkableRealtimeEventKeyCriteria(null, "type", "value-too-long"),
            ]),
            Authorization(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-list-length",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(DefinitionNames: ["definition-too-long"]),
            Authorization(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-key-count",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(Keys:
            [
                new WorkableRealtimeEventKeyCriteria(null, "type", "first"),
                new WorkableRealtimeEventKeyCriteria(null, "type", "second"),
            ]),
            Authorization(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-null-key",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(Keys: [null!]),
            Authorization(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => subscriptions.WatchEvents(
            "connection-key-type-length",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(Keys:
            [
                new WorkableRealtimeEventKeyCriteria(null, "type-too-long", "value"),
            ]),
            Authorization(),
            CancellationToken.None));

        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
        Assert.Empty(groups.Adds);
    }

    [Fact]
    public async Task ReplaceExistingGroupWhenReadScopeChanges()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var firstDefinitionId = DefinitionId(system, "signalr.subscription.first");
        var secondDefinitionId = DefinitionId(system, "signalr.subscription.second");

        var firstWatch = subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(readableDefinitionIds: [firstDefinitionId]),
            CancellationToken.None);
        var firstAdded = await groups.WaitForAdd();
        subscriptions.SetStreaming(firstAdded.GroupName, isStreaming: true);
        await firstWatch.WaitAsync(TimeSpan.FromSeconds(1));

        var secondWatch = subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(readableDefinitionIds: [secondDefinitionId]),
            CancellationToken.None);
        var secondAdded = await groups.WaitForAdd();
        subscriptions.SetStreaming(secondAdded.GroupName, isStreaming: true);
        await secondWatch.WaitAsync(TimeSpan.FromSeconds(1));

        var removed = Assert.Single(groups.Removes);
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

        Assert.NotEqual(firstAdded.GroupName, secondAdded.GroupName);
        Assert.Equal(firstAdded.GroupName, removed.GroupName);
        Assert.Equal(secondAdded.GroupName, snapshot.GroupName);
        Assert.Equal("connection-1", snapshot.ConnectionId);
    }

    [Fact]
    public async Task RemoveMatchingGroupWhenEventsAreUnwatched()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = new WorkableRealtimeEventCriteria(["worker.completed"]);

        var watch = subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            criteria,
            Authorization(),
            CancellationToken.None);
        var added = await groups.WaitForAdd();
        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        await watch.WaitAsync(TimeSpan.FromSeconds(1));

        await subscriptions.UnwatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            criteria,
            CancellationToken.None);

        var removed = Assert.Single(groups.Removes);
        Assert.Equal(added.GroupName, removed.GroupName);
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
    }

    [Fact]
    public async Task ShareIdenticalEventGroupsAcrossConnectionsAndIgnoreDuplicateWatches()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var criteria = new WorkableRealtimeEventCriteria(["worker.completed"]);

        await WatchAndStart(subscriptions, groups, system, "connection-1", criteria);
        await WatchAndStart(subscriptions, groups, system, "connection-2", criteria);
        var addCount = groups.Adds.Count;

        await subscriptions.WatchEvents(
            "connection-2",
            groups,
            system,
            system.Catalog,
            criteria,
            Authorization(),
            CancellationToken.None);

        Assert.Equal(addCount, groups.Adds.Count);
        Assert.Single(subscriptions.GetActiveSubscriptions(system));
        Assert.All(
            subscriptions.GetSubscriptionSnapshots(system),
            snapshot => Assert.Equal(2, snapshot.GroupConnectionCount));

        subscriptions.RemoveConnection("connection-1");
        Assert.Equal(1, Assert.Single(subscriptions.GetSubscriptionSnapshots(system)).GroupConnectionCount);
        await subscriptions.UnwatchEvents(
            "connection-2",
            groups,
            system,
            system.Catalog,
            criteria,
            CancellationToken.None);
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
    }

    [Fact]
    public async Task RemoveEveryGroupForDisconnectedConnection()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();

        await WatchAndStart(subscriptions, groups, system, "connection-1", new WorkableRealtimeEventCriteria(["worker.queued"]));
        await WatchAndStart(subscriptions, groups, system, "connection-1", new WorkableRealtimeEventCriteria(["worker.completed"]));
        await WatchAndStart(subscriptions, groups, system, "connection-2", new WorkableRealtimeEventCriteria(["worker.failed"]));

        subscriptions.RemoveConnection("connection-1");

        Assert.Empty(groups.Removes);
        Assert.Collection(
            subscriptions.GetSubscriptionSnapshots(system),
            snapshot => Assert.Equal("connection-2", snapshot.ConnectionId));
    }

    [Fact]
    public async Task RollBackSubscriptionWhenGroupAddFails()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager { FailAdds = true };
        await using var system = CreateSystem();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            system.Catalog,
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(),
            CancellationToken.None));

        Assert.Equal("Add failed.", exception.Message);
        Assert.Empty(subscriptions.GetSubscriptionSnapshots(system));
        Assert.Empty(groups.Removes);
    }

    [Fact]
    public async Task KeepNewerSubscriptionWhenPreviousGroupAddFailsAfterReplacement()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new DelayedFirstAddGroupManager();
        await using var system = CreateSystem();
        var criteria = new WorkableRealtimeEventCriteria(["worker.completed"]);

        var first = subscriptions.WatchEvents(
            "connection-1", groups, system, system.Catalog, criteria,
            Authorization(readableDefinitionIds: [DefinitionId(system, "signalr.subscription.first")]),
            CancellationToken.None);
        await groups.WaitForFirstAdd();

        var second = subscriptions.WatchEvents(
            "connection-1", groups, system, system.Catalog, criteria,
            Authorization(readableDefinitionIds: [DefinitionId(system, "signalr.subscription.second")]),
            CancellationToken.None);
        var secondAdd = await groups.WaitForSecondAdd();
        subscriptions.SetStreaming(secondAdd.GroupName, isStreaming: true);
        await second;

        groups.FailFirstAdd();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var snapshot = Assert.Single(subscriptions.GetSubscriptionSnapshots(system));

        Assert.Equal("First add failed.", exception.Message);
        Assert.Equal(secondAdd.GroupName, snapshot.GroupName);
        Assert.Equal(secondAdd.GroupName, Assert.Single(subscriptions.GetActiveSubscriptions(system)).GroupName);
    }

    private static async Task WatchAndStart(
        WorkableRealtimeEventSubscriptions subscriptions,
        RecordingSignalRGroupManager groups,
        IWorkSystem system,
        string connectionId,
        WorkableRealtimeEventCriteria criteria)
    {
        var watch = subscriptions.WatchEvents(
            connectionId,
            groups,
            system,
            system.Catalog,
            criteria,
            Authorization(),
            CancellationToken.None);
        var added = await groups.WaitForAdd();
        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        await watch.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static IWorkSystem CreateSystem()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("signalr.subscription.first"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                WorkDefinition.Create("signalr.subscription.second"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        });
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private static WorkDefinitionId DefinitionId(IWorkSystem system, string name)
        => system.Catalog.Definitions.Single(definition => definition.Name == name).Id;

    private static IReadOnlySet<T> Required<T>(IReadOnlySet<T>? values)
        => values ?? throw new InvalidOperationException("Expected filter values.");

    private static WorkAuthorizationSnapshot Authorization(IReadOnlyList<WorkDefinitionId>? readableDefinitionIds = null)
        => WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            new WorkActor("signalr-subscription-user", "SignalR Subscription User"),
            ["signalr.read"],
            readableDefinitionIds);

    private static string FilterKey(WorkEventFilter? filter)
    {
        var method = typeof(WorkableRealtimeEventSubscriptions).GetMethod(
            "CreateFilterKey",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [filter]));
    }

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

using Microsoft.Extensions.DependencyInjection;

namespace Workable.Tests;

public sealed class WorkableRealtimeEventSubscriptionsShould
{
    [Fact]
    public async Task NormalizeCriteriaWhenCreatingEventGroups()
    {
        var subscriptions = new WorkableRealtimeEventSubscriptions();
        var groups = new RecordingSignalRGroupManager();
        await using var system = CreateSystem();
        var definitionId = DefinitionId(system, "signalr.subscription.first");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var watch = subscriptions.WatchEvents(
            "connection-1",
            groups,
            system,
            new WorkableRealtimeEventCriteria(
                EventTypes: [" worker.completed ", "WORKER.COMPLETED", " "],
                DefinitionIds: [definitionId.Value.ToString("D"), "not-a-guid", definitionId.Value.ToString("N")],
                Keys:
                [
                    new WorkableRealtimeEventKeyCriteria(WorkKeyKind.Identifier, " batch ", " accepted "),
                    new WorkableRealtimeEventKeyCriteria(WorkKeyKind.Identifier, "batch", "accepted"),
                    new WorkableRealtimeEventKeyCriteria(null, " ", "ignored"),
                ]),
            Authorization(readableDefinitionIds: [definitionId]),
            timeout.Token);

        var added = await groups.WaitForAdd();
        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));
        var filter = snapshot.Filter ?? throw new InvalidOperationException("Expected filtered subscription.");
        var key = Assert.Single(Required(filter.Keys));

        Assert.False(watch.IsCompleted);
        Assert.Equal("connection-1", snapshot.ConnectionId);
        Assert.Equal(added.GroupName, snapshot.GroupName);
        Assert.Equal(1, snapshot.GroupConnectionCount);
        Assert.False(snapshot.IsStreaming);
        Assert.Equal(["worker.completed"], Required(filter.EventTypes).ToArray());
        Assert.Equal([definitionId], Required(filter.DefinitionIds).ToArray());
        Assert.Equal(WorkKeyKind.Identifier, key.Kind);
        Assert.Equal("batch", key.Type);
        Assert.Equal("accepted", key.Value);

        subscriptions.SetStreaming(added.GroupName, isStreaming: true);
        await watch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(Assert.Single(subscriptions.GetDebugSubscriptions(system)).IsStreaming);
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
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(readableDefinitionIds: [secondDefinitionId]),
            CancellationToken.None);
        var secondAdded = await groups.WaitForAdd();
        subscriptions.SetStreaming(secondAdded.GroupName, isStreaming: true);
        await secondWatch.WaitAsync(TimeSpan.FromSeconds(1));

        var removed = Assert.Single(groups.Removes);
        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));

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
            criteria,
            CancellationToken.None);

        var removed = Assert.Single(groups.Removes);
        Assert.Equal(added.GroupName, removed.GroupName);
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
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

        await subscriptions.RemoveConnection("connection-1", groups, CancellationToken.None);

        Assert.Equal(2, groups.Removes.Count);
        Assert.All(groups.Removes, remove => Assert.Equal("connection-1", remove.ConnectionId));
        Assert.Collection(
            subscriptions.GetDebugSubscriptions(system),
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
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            Authorization(),
            CancellationToken.None));

        Assert.Equal("Add failed.", exception.Message);
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
        Assert.Empty(groups.Removes);
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
        => WorkAuthorizationSnapshot.Create(
            new WorkActor("signalr-subscription-user", "SignalR Subscription User"),
            ["signalr.read"],
            readableDefinitionIds);

}

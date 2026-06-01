using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeViewSubscriptionsShould
{
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
        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));

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
        Assert.Single(subscriptions.GetDebugSubscriptions(system));
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
        var snapshot = Assert.Single(subscriptions.GetDebugSubscriptions(system));

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
        Assert.All(subscriptions.GetDebugSubscriptions(system), snapshot => Assert.Equal(2, snapshot.GroupConnectionCount));
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
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
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
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
        Assert.Empty(subscriptions.GetActiveSubscriptions(system));
        Assert.Empty(groups.Removes);
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

    private static IWorkSystem CreateSystem()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            WorkDefinition.Create("signalr.view.subscription"),
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success())));
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private static WorkAuthorizationSnapshot Authorization()
        => WorkAuthorizationSnapshot.Create(
            new WorkActor("signalr-view-subscription-user", "SignalR View Subscription User"),
            ["signalr.read"],
            readableDefinitionIds: null);
}

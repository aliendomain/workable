using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "ChangeStream")]
public sealed class WorkChangeStreamTests
{
    [Fact]
    public async Task SubscriptionReceivesChangesPublishedAfterSubscribe()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var key = WorkChangeKey.Worker(WorkerId.New());

        stream.Publish(key);

        var change = await ReadNext(reader);
        Assert.Equal(1, change.Sequence);
        Assert.Equal(key, change.Key);
        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(1, diagnostics.AcceptedChangeCount);
        Assert.Equal(1, diagnostics.DeliveredChangeCount);
    }

    [Fact]
    public async Task ChangesPublishedBeforeSubscribeAreNotReplayed()
    {
        var stream = new WorkChangeStream();

        stream.Publish(WorkChangeKey.Worker(WorkerId.New()));

        await using var subscription = stream.Subscribe();

        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(0, diagnostics.AcceptedChangeCount);
    }

    [Fact]
    public void PublishWithoutSubscribersDoesNotRetainChangesForFutureSubscribers()
    {
        var stream = new WorkChangeStream();

        stream.Publish(WorkChangeKey.Worker(WorkerId.New()));
        stream.Publish(WorkChangeKey.Definition("invoice.close"));

        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task RepeatedSameKeyCoalescesToLatestChange()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var key = WorkChangeKey.Worker(WorkerId.New());

        stream.Publish(key);
        stream.Publish(key);

        var change = await ReadNext(reader);
        Assert.Equal(2, change.Sequence);
        Assert.Equal(key, change.Key);
        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(2, diagnostics.AcceptedChangeCount);
        Assert.Equal(1, diagnostics.CoalescedChangeCount);
        Assert.Equal(1, diagnostics.DeliveredChangeCount);
        Assert.Equal(0, diagnostics.DroppedChangeCount);
    }

    [Fact]
    public async Task DefinitionScopeDoesNotChangePublicEqualityButPreventsCrossScopeCoalescing()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var firstKey = WorkChangeKey.System().ScopeToDefinition("first.work");
        var secondKey = WorkChangeKey.System().ScopeToDefinition("second.work");

        Assert.Equal(firstKey, secondKey);
        Assert.Equal(firstKey.GetHashCode(), secondKey.GetHashCode());

        stream.Publish(firstKey);
        stream.Publish(secondKey);

        var first = await ReadNext(reader);
        var second = await ReadNext(reader);
        Assert.Equal("first.work", first.Key.DefinitionName);
        Assert.Equal("second.work", second.Key.DefinitionName);
        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(0, diagnostics.CoalescedChangeCount);
        Assert.Equal(2, diagnostics.DeliveredChangeCount);
    }

    [Fact]
    public async Task DifferentKeysArePreservedInPublishOrder()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workerKey = WorkChangeKey.Worker(WorkerId.New());
        var definitionKey = WorkChangeKey.Definition("invoice.close");

        stream.Publish(workerKey);
        stream.Publish(definitionKey);

        var first = await ReadNext(reader);
        var second = await ReadNext(reader);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(workerKey, first.Key);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(definitionKey, second.Key);
        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(2, diagnostics.AcceptedChangeCount);
        Assert.Equal(2, diagnostics.DeliveredChangeCount);
    }

    [Fact]
    public async Task BoundedPendingChangesDropOldestDistinctKeys()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe(new WorkChangeSubscriptionOptions(Capacity: 2));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var firstKey = WorkChangeKey.Worker(WorkerId.New());
        var secondKey = WorkChangeKey.Worker(WorkerId.New());
        var thirdKey = WorkChangeKey.Worker(WorkerId.New());

        stream.Publish(firstKey);
        stream.Publish(secondKey);
        stream.Publish(thirdKey);

        var firstRead = await ReadNext(reader);
        var secondRead = await ReadNext(reader);
        Assert.Equal(2, firstRead.Sequence);
        Assert.Equal(secondKey, firstRead.Key);
        Assert.Equal(3, secondRead.Sequence);
        Assert.Equal(thirdKey, secondRead.Key);
        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(3, diagnostics.AcceptedChangeCount);
        Assert.Equal(2, diagnostics.DeliveredChangeCount);
        Assert.Equal(1, diagnostics.DroppedChangeCount);
        Assert.Equal(2, diagnostics.PeakQueuedCount);
    }

    [Fact]
    public async Task CoalescingDoesNotIncreasePendingCapacityPressure()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe(new WorkChangeSubscriptionOptions(Capacity: 1));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var key = WorkChangeKey.Worker(WorkerId.New());

        stream.Publish(key);
        stream.Publish(key);

        var change = await ReadNext(reader);
        Assert.Equal(2, change.Sequence);
        Assert.Equal(key, change.Key);
        var diagnostics = AssertNoQueuedChanges(subscription);
        Assert.Equal(2, diagnostics.AcceptedChangeCount);
        Assert.Equal(1, diagnostics.CoalescedChangeCount);
        Assert.Equal(0, diagnostics.DroppedChangeCount);
        Assert.Equal(1, diagnostics.PeakQueuedCount);
    }

    [Fact]
    public async Task SlowSubscriberDoesNotPreventOtherSubscribersFromReceivingChanges()
    {
        var stream = new WorkChangeStream();
        await using var slow = stream.Subscribe(new WorkChangeSubscriptionOptions(Capacity: 1));
        await using var fast = stream.Subscribe(new WorkChangeSubscriptionOptions(Capacity: 3));
        await using var fastReader = fast.Read().GetAsyncEnumerator();
        var firstKey = WorkChangeKey.Worker(WorkerId.New());
        var secondKey = WorkChangeKey.Worker(WorkerId.New());
        var thirdKey = WorkChangeKey.Worker(WorkerId.New());

        stream.Publish(firstKey);
        stream.Publish(secondKey);
        stream.Publish(thirdKey);

        Assert.Equal(firstKey, (await ReadNext(fastReader)).Key);
        Assert.Equal(secondKey, (await ReadNext(fastReader)).Key);
        Assert.Equal(thirdKey, (await ReadNext(fastReader)).Key);

        var slowDiagnostics = GetDiagnostics(slow);
        Assert.Equal(1, slowDiagnostics.QueuedCount);
        Assert.Equal(2, slowDiagnostics.DroppedChangeCount);
    }

    [Fact]
    public async Task RemovingAMiddleSubscriberKeepsTheRemainingSubscribersOrderedAndActive()
    {
        var stream = new WorkChangeStream();
        await using var first = stream.Subscribe();
        var middle = stream.Subscribe();
        await using var last = stream.Subscribe();
        await using var firstReader = first.Read().GetAsyncEnumerator();
        await using var lastReader = last.Read().GetAsyncEnumerator();
        var key = WorkChangeKey.Worker(WorkerId.New());

        await middle.DisposeAsync();
        stream.Publish(key);

        Assert.Equal(2, stream.ActiveSubscriptionCount);
        Assert.Equal(key, (await ReadNext(firstReader)).Key);
        Assert.Equal(key, (await ReadNext(lastReader)).Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkSubscriptionAndEnumeratorCancellationTokens(bool cancelSubscriptionToken)
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        using var subscriptionCancellation = new CancellationTokenSource();
        using var enumeratorCancellation = new CancellationTokenSource();
        var reader = subscription
            .Read(subscriptionCancellation.Token)
            .GetAsyncEnumerator(enumeratorCancellation.Token);
        var read = reader.MoveNextAsync().AsTask();

        if (cancelSubscriptionToken)
        {
            await subscriptionCancellation.CancelAsync();
        }
        else
        {
            await enumeratorCancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await reader.DisposeAsync();
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposingSubscriptionCompletesPendingReader()
    {
        var stream = new WorkChangeStream();
        var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.False(read.IsCompleted);

        await subscription.DisposeAsync();

        Assert.False(await ReadCompletion(read));
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposingReaderRemovesSubscription()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        var reader = subscription.Read().GetAsyncEnumerator();

        await reader.DisposeAsync();

        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposingStreamCompletesPendingReaders()
    {
        var stream = new WorkChangeStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.False(read.IsCompleted);

        await stream.DisposeAsync();

        Assert.False(await ReadCompletion(read));
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposedStreamIsIdempotentRejectsSubscriptionsAndIgnoresPublications()
    {
        var stream = new WorkChangeStream();
        await stream.DisposeAsync();

        stream.Publish(WorkChangeKey.System());
        await stream.DisposeAsync();

        Assert.Equal(0, stream.ActiveSubscriptionCount);
        Assert.Throws<ObjectDisposedException>(() => stream.Subscribe());
    }

    [Fact]
    public async Task RemovedAndDisposedSubscriptionsIgnoreOwnerAndLatePublicationCallbacks()
    {
        var stream = new WorkChangeStream();
        var subscription = stream.Subscribe();
        var subscriptionType = subscription.GetType();
        var remove = typeof(WorkChangeStream).GetMethod("Remove", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var publish = subscriptionType.GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var change = new WorkChange(1, DateTimeOffset.UtcNow, WorkChangeKey.System());

        remove.Invoke(stream, [subscription]);
        remove.Invoke(stream, [subscription]);
        await subscription.DisposeAsync();
        publish.Invoke(subscription, [change]);

        Assert.Equal(0, stream.ActiveSubscriptionCount);
        Assert.Equal(0, GetDiagnostics(subscription).AcceptedChangeCount);
    }

    [Fact]
    public async Task ReaderCancellationCompositionUsesEnumeratorSubscriptionAndLinkedTokens()
    {
        var stream = new WorkChangeStream();
        using var subscriptionCancellation = new CancellationTokenSource();
        using var enumeratorCancellation = new CancellationTokenSource();
        await using var subscription = stream.Subscribe();

        await using (var enumeratorOnly = subscription.Read()
            .GetAsyncEnumerator(enumeratorCancellation.Token))
        {
            enumeratorCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                enumeratorOnly.MoveNextAsync().AsTask());
        }

        await using var second = stream.Subscribe();
        await using (var subscriptionOnly = second.Read(subscriptionCancellation.Token)
            .GetAsyncEnumerator())
        {
            subscriptionCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                subscriptionOnly.MoveNextAsync().AsTask());
        }

        using var sharedCancellation = new CancellationTokenSource();
        await using var third = stream.Subscribe();
        await using (var shared = third.Read(sharedCancellation.Token)
            .GetAsyncEnumerator(sharedCancellation.Token))
        {
            sharedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                shared.MoveNextAsync().AsTask());
        }

        using var firstLinked = new CancellationTokenSource();
        using var secondLinked = new CancellationTokenSource();
        await using var fourth = stream.Subscribe();
        await using (var linked = fourth.Read(firstLinked.Token)
            .GetAsyncEnumerator(secondLinked.Token))
        {
            secondLinked.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                linked.MoveNextAsync().AsTask());
        }
    }

    [Fact]
    public void RejectsNonPositiveSubscriptionCapacity()
    {
        var stream = new WorkChangeStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Subscribe(new WorkChangeSubscriptionOptions(Capacity: 0)));
    }

    [Fact]
    public async Task InMemoryWorkSystemExposesOwnedChangeStream()
    {
        await using var system = CreateInMemorySystem();
        var stream = system.ChangeStream;
        await using var subscription = system.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var key = WorkChangeKey.System();

        stream.Publish(key);

        var change = await ReadNext(reader);
        Assert.Equal(key, change.Key);
        Assert.Equal(1, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposingInMemoryWorkSystemDisposesOwnedChangeStream()
    {
        var system = CreateInMemorySystem();
        var stream = system.ChangeStream;
        await using var subscription = system.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.False(read.IsCompleted);

        await system.DisposeAsync();

        while (await ReadCompletion(read))
        {
            read = reader.MoveNextAsync().AsTask();
        }

        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task InMemoryWorkSystemSessionExposesOwnedChangeStream()
    {
        await using var system = CreateInMemorySystem();
        var session = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await using var subscription = session.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var key = WorkChangeKey.System();

        system.ChangeStream.Publish(key);

        var change = await ReadNext(reader);
        Assert.Equal(key, change.Key);
    }

    private static async Task<WorkChange> ReadNext(IAsyncEnumerator<WorkChange> reader)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = reader.MoveNextAsync().AsTask();
        var completed = await Task.WhenAny(read, Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token));
        Assert.Same(read, completed);
        Assert.True(await read);
        return reader.Current;
    }

    private static async Task<bool> ReadCompletion(Task<bool> read)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(read, Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token));
        Assert.Same(read, completed);
        return await read;
    }

    private static WorkChangeSubscriptionDiagnosticsSnapshot AssertNoQueuedChanges(IWorkChangeSubscription subscription)
    {
        var diagnostics = GetDiagnostics(subscription);
        Assert.Equal(0, diagnostics.QueuedCount);
        return diagnostics;
    }

    private static WorkChangeSubscriptionDiagnosticsSnapshot GetDiagnostics(IWorkChangeSubscription subscription)
        => Assert
            .IsAssignableFrom<IWorkChangeSubscriptionDiagnostics>(subscription)
            .GetDiagnosticsSnapshot();

    private static InMemoryWorkSystem CreateInMemorySystem()
        => Assert.IsType<InMemoryWorkSystem>(new ServiceCollection()
            .AddWorkableSystem(builder => { })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default);
}

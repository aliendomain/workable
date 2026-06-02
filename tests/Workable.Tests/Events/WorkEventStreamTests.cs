using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "EventStream")]
public sealed class WorkEventStreamTests
{
    [Fact]
    public async Task SubscriptionReceivesEventsPublishedAfterSubscribe()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(eventType: "worker.queued");

        stream.Publish(workEvent);

        Assert.Equal(workEvent, await ReadNext(reader));
    }

    [Fact]
    public async Task EventsPublishedBeforeSubscribeAreNotReplayed()
    {
        var stream = new WorkEventStream();
        stream.Publish(CreateEvent(eventType: "worker.queued"));

        await using var subscription = stream.Subscribe();

        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public async Task PublishWithoutSubscribersDoesNotRetainEventsForFutureSubscribers()
    {
        var stream = new WorkEventStream();

        stream.Publish(CreateEvent(eventType: "worker.queued"));
        stream.Publish(CreateEvent(eventType: "worker.started"));

        Assert.Equal(0, stream.ActiveSubscriptionCount);

        await using var subscription = stream.Subscribe();

        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public void LazyPublishWithoutSubscribersDoesNotCreateEvent()
    {
        var stream = new WorkEventStream();
        var created = false;
        var workEvent = CreateEvent(eventType: "worker.queued");

        stream.Publish(
            workEvent,
            state =>
            {
                created = true;
                return state;
            });

        Assert.False(created);
    }

    [Fact]
    public void LazyPublishWithMetadataFactoryWithoutSubscribersDoesNotCreateMetadataOrEvent()
    {
        var stream = new WorkEventStream();
        var metadataCreated = false;
        var eventCreated = false;
        var workEvent = CreateEvent(eventType: "worker.queued");

        stream.Publish(
            workEvent,
            state =>
            {
                metadataCreated = true;
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.SubjectId,
                    state.ConcurrencyKey,
                    state.EventType);
            },
            state =>
            {
                eventCreated = true;
                return state;
            });

        Assert.False(metadataCreated);
        Assert.False(eventCreated);
    }

    [Fact]
    public async Task LazyPublishWithMetadataFactoryDeliversUnfilteredSubscribersWithoutCreatingMetadata()
    {
        var stream = new WorkEventStream();
        var metadataCreated = false;
        var eventCreated = false;
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(eventType: "worker.queued");

        stream.Publish(
            workEvent,
            state =>
            {
                metadataCreated = true;
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.SubjectId,
                    state.ConcurrencyKey,
                    state.EventType);
            },
            state =>
            {
                eventCreated = true;
                return state;
            });

        Assert.False(metadataCreated);
        Assert.True(eventCreated);
        Assert.Equal(workEvent, await ReadNext(reader));
    }

    [Fact]
    public async Task LazyPublishWithMetadataFactoryCreatesMetadataForFilteredSubscribers()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        var metadataCreated = false;
        var eventCreated = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: acceptedWorkerId));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(workerId: acceptedWorkerId, eventType: "worker.queued");

        stream.Publish(
            workEvent,
            state =>
            {
                metadataCreated = true;
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.SubjectId,
                    state.ConcurrencyKey,
                    state.EventType);
            },
            state =>
            {
                eventCreated = true;
                return state;
            });

        Assert.True(metadataCreated);
        Assert.True(eventCreated);
        Assert.Equal(workEvent, await ReadNext(reader));
    }

    [Fact]
    public async Task LazyPublishWithMetadataFactoryCreatesMetadataBeforeEventForMixedSubscribers()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        var order = new List<string>();
        await using var unfiltered = stream.Subscribe();
        await using var filtered = stream.Subscribe(new WorkEventFilter(WorkerId: acceptedWorkerId));
        await using var unfilteredReader = unfiltered.Read().GetAsyncEnumerator();
        await using var filteredReader = filtered.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(workerId: acceptedWorkerId, eventType: "worker.queued");

        stream.Publish(
            workEvent,
            state =>
            {
                order.Add("metadata");
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.SubjectId,
                    state.ConcurrencyKey,
                    state.EventType);
            },
            state =>
            {
                order.Add("event");
                return state;
            });

        Assert.Equal(new[] { "metadata", "event" }, order);
        Assert.Equal(workEvent, await ReadNext(unfilteredReader));
        Assert.Equal(workEvent, await ReadNext(filteredReader));
    }

    [Fact]
    public async Task LazyPublishWithMetadataDoesNotCreateEventWhenFiltersDoNotMatch()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: acceptedWorkerId));
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            null,
            "worker.queued",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { new("invoice", "inv-100") };
            });

        stream.Publish(
            metadata,
            CreateEvent(eventType: "worker.queued"),
            state =>
            {
                created = true;
                return state;
            });

        Assert.False(created);
        Assert.False(loadedIdentifiers);
        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public async Task LazyPublishWithMetadataCreatesEventWhenFilterMatches()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: acceptedWorkerId));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(workerId: acceptedWorkerId, eventType: "worker.started");
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            acceptedWorkerId,
            WorkDefinitionId.New(),
            null,
            null,
            "worker.started");

        stream.Publish(
            metadata,
            workEvent,
            state =>
            {
                created = true;
                return state;
            });

        Assert.True(created);
        Assert.Equal(workEvent, await ReadNext(reader));
    }

    [Fact]
    public async Task PublishRequiresEvent()
    {
        var stream = new WorkEventStream();

        await using var _ = stream;
        var exception = Assert.Throws<ArgumentNullException>(() => stream.Publish(null!));

        Assert.Equal("workEvent", exception.ParamName);
    }

    [Fact]
    public async Task EventsAreBroadcastToEveryActiveSubscription()
    {
        var stream = new WorkEventStream();
        await using var first = stream.Subscribe();
        await using var second = stream.Subscribe();
        await using var firstReader = first.Read().GetAsyncEnumerator();
        await using var secondReader = second.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(eventType: "worker.completed");

        stream.Publish(workEvent);

        Assert.Equal(workEvent, await ReadNext(firstReader));
        Assert.Equal(workEvent, await ReadNext(secondReader));
    }

    [Fact]
    public async Task FiltersByWorker()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: acceptedWorkerId));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(workerId: WorkerId.New(), eventType: "worker.queued");
        var accepted = CreateEvent(workerId: acceptedWorkerId, eventType: "worker.started");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FiltersByDefinition()
    {
        var stream = new WorkEventStream();
        var acceptedDefinitionId = WorkDefinitionId.New();
        await using var subscription = stream.Subscribe(new WorkEventFilter(DefinitionId: acceptedDefinitionId));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(definitionId: WorkDefinitionId.New(), eventType: "worker.queued");
        var accepted = CreateEvent(definitionId: acceptedDefinitionId, eventType: "worker.queued");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FiltersByEventTypeIgnoringCase()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(new WorkEventFilter(EventType: "WORKER.COMPLETED"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(eventType: "worker.started");
        var accepted = CreateEvent(eventType: "worker.completed");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FiltersByEventTypesIgnoringCase()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            EventTypes: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "WORKER.COMPLETED",
                "worker.failed",
            }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(eventType: "worker.started");
        var accepted = CreateEvent(eventType: "worker.completed");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FiltersByDefinitionIds()
    {
        var stream = new WorkEventStream();
        var acceptedDefinitionId = WorkDefinitionId.New();
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            DefinitionIds: new HashSet<WorkDefinitionId> { acceptedDefinitionId }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(definitionId: WorkDefinitionId.New(), eventType: "worker.queued");
        var accepted = CreateEvent(definitionId: acceptedDefinitionId, eventType: "worker.queued");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FilterRequiresAllSpecifiedValuesToMatch()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        var acceptedDefinitionId = WorkDefinitionId.New();
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            WorkerId: acceptedWorkerId,
            DefinitionId: acceptedDefinitionId,
            EventType: "worker.completed"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignoredByDefinition = CreateEvent(acceptedWorkerId, WorkDefinitionId.New(), eventType: "worker.completed");
        var ignoredByType = CreateEvent(acceptedWorkerId, acceptedDefinitionId, eventType: "worker.failed");
        var accepted = CreateEvent(acceptedWorkerId, acceptedDefinitionId, eventType: "worker.completed");

        stream.Publish(ignoredByDefinition);
        stream.Publish(ignoredByType);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FiltersByIdentifier()
    {
        var stream = new WorkEventStream();
        var identifier = new WorkIdentifier("invoice", "inv-100");
        await using var subscription = stream.Subscribe(new WorkEventFilter(Identifier: identifier));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(eventType: "worker.queued");
        var accepted = CreateEvent(eventType: "worker.started", identifiers: new HashSet<WorkIdentifier> { identifier });

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task LazyPublishWithMetadataFiltersByKeyBeforeCreatingEvent()
    {
        var stream = new WorkEventStream();
        var acceptedIdentifier = new WorkIdentifier("invoice", "inv-100");
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter>
            {
                new(WorkKeyKind.Identifier, acceptedIdentifier.Type, acceptedIdentifier.Value),
            }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            null,
            "worker.queued",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { acceptedIdentifier };
            });

        stream.Publish(
            metadata,
            CreateEvent(eventType: "worker.queued", identifiers: new HashSet<WorkIdentifier> { acceptedIdentifier }),
            state =>
            {
                created = true;
                return state;
            });

        var workEvent = await ReadNext(reader);

        Assert.True(loadedIdentifiers);
        Assert.True(created);
        Assert.Equal(acceptedIdentifier, Assert.Single(workEvent.Identifiers));
    }

    [Fact]
    public async Task LazyPublishWithMetadataDoesNotCreateEventWhenKeyFilterDoesNotMatch()
    {
        var stream = new WorkEventStream();
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter>
            {
                new(WorkKeyKind.Identifier, "invoice", "inv-100"),
            }));
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            null,
            "worker.queued",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { new("invoice", "inv-200") };
            });

        stream.Publish(
            metadata,
            CreateEvent(eventType: "worker.queued"),
            state =>
            {
                created = true;
                return state;
            });

        Assert.True(loadedIdentifiers);
        Assert.False(created);
        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public async Task DisposedSubscriptionStopsReceivingEventsAndIsRemoved()
    {
        var stream = new WorkEventStream();
        var subscription = stream.Subscribe();

        Assert.Equal(1, stream.ActiveSubscriptionCount);

        await subscription.DisposeAsync();
        stream.Publish(CreateEvent(eventType: "worker.queued"));

        Assert.Equal(0, stream.ActiveSubscriptionCount);
        await AssertReadAlreadyCompleted(subscription);
    }

    [Fact]
    public async Task CancelledReadRemovesSubscription()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe();

        Assert.Equal(1, stream.ActiveSubscriptionCount);

        await CancelRead(subscription);

        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposingStreamCompletesActiveReadersAndRemovesSubscriptions()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.False(read.IsCompleted);

        await stream.DisposeAsync();

        Assert.False(await ReadCompletion(read));
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposedStreamRejectsNewSubscriptions()
    {
        var stream = new WorkEventStream();

        await stream.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => stream.Subscribe());
    }

    [Fact]
    public async Task SubscriptionCapacityIsRequiredToBePositive()
    {
        var stream = new WorkEventStream();

        await using var _ = stream;
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: 0)));
    }

    [Fact]
    public async Task BoundedSubscriptionsDropOldestByDefault()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: 2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.queued");
        var second = CreateEvent(eventType: "worker.started");
        var third = CreateEvent(eventType: "worker.completed");

        stream.Publish(first);
        stream.Publish(second);
        stream.Publish(third);

        Assert.Equal(second, await ReadNext(reader));
        Assert.Equal(third, await ReadNext(reader));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(3, diagnostics.AcceptedEventCount);
        Assert.Equal(1, diagnostics.DroppedEventCount);
    }

    [Fact]
    public async Task BoundedSubscriptionsCanDropNewest()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(options: new WorkEventSubscriptionOptions(
            Capacity: 2,
            OverflowBehavior: WorkEventOverflowBehavior.DropNewest));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.queued");
        var second = CreateEvent(eventType: "worker.started");
        var third = CreateEvent(eventType: "worker.completed");

        stream.Publish(first);
        stream.Publish(second);
        stream.Publish(third);

        Assert.Equal(first, await ReadNext(reader));
        Assert.Equal(third, await ReadNext(reader));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(3, diagnostics.AcceptedEventCount);
        Assert.Equal(1, diagnostics.DroppedEventCount);
    }

    [Fact]
    public async Task BoundedSubscriptionsCanDropWrites()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(options: new WorkEventSubscriptionOptions(
            Capacity: 2,
            OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.queued");
        var second = CreateEvent(eventType: "worker.started");
        var third = CreateEvent(eventType: "worker.completed");

        stream.Publish(first);
        stream.Publish(second);
        stream.Publish(third);

        Assert.Equal(first, await ReadNext(reader));
        Assert.Equal(second, await ReadNext(reader));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(2, diagnostics.AcceptedEventCount);
        Assert.Equal(1, diagnostics.DroppedEventCount);
    }

    [Fact]
    public async Task LazyPublishDoesNotCreateEventForFullDropWriteSubscription()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(
            options: new WorkEventSubscriptionOptions(
                Capacity: 1,
                OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.started");
        var second = CreateEvent(eventType: "worker.completed");
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            null,
            "worker.completed");
        var created = 0;

        stream.Publish(metadata, first, state =>
        {
            created++;
            return state;
        });
        stream.Publish(metadata, second, state =>
        {
            created++;
            return state;
        });

        Assert.Equal(1, created);
        Assert.Equal(first, await ReadNext(reader));
        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(1, diagnostics.AcceptedEventCount);
        Assert.Equal(0, diagnostics.DroppedEventCount);
    }

    [Fact]
    public async Task SlowSubscriberDoesNotPreventOtherSubscribersFromReceivingEvents()
    {
        var stream = new WorkEventStream();
        await using var slow = stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: 1));
        await using var fast = stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: 4));
        await using var fastReader = fast.Read().GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.queued");
        var second = CreateEvent(eventType: "worker.started");
        var third = CreateEvent(eventType: "worker.completed");

        stream.Publish(first);
        stream.Publish(second);
        stream.Publish(third);

        Assert.Equal(first, await ReadNext(fastReader));
        Assert.Equal(second, await ReadNext(fastReader));
        Assert.Equal(third, await ReadNext(fastReader));

        await slow.DisposeAsync();
    }

    [Fact]
    public async Task SystemEventsExposeSubscriptions()
    {
        var definition = WorkDefinition.Create("observe-queue", "Publishes a queued event.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.queued"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("observe-queue");
        var workEvent = await ReadNext(reader);

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(handle.WorkerId, workEvent.WorkerId);
        Assert.Equal(definition.Name, workEvent.WorkDefinitionName);
        Assert.Equal("worker.queued", workEvent.EventType);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private static WorkEventSubscriptionDiagnosticsSnapshot AssertNoQueuedEvents(IWorkEventSubscription subscription)
    {
        var diagnostics = Assert
            .IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription)
            .GetDiagnosticsSnapshot();

        Assert.Equal(0, diagnostics.QueuedCount);
        return diagnostics;
    }

    private static async Task CancelRead(IWorkEventSubscription subscription)
    {
        using var cancellation = new CancellationTokenSource();
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();

        var read = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
    }

    private static async Task<bool> ReadCompletion(Task<bool> read)
        => await read.WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task AssertReadAlreadyCompleted(IWorkEventSubscription subscription)
    {
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.True(read.IsCompleted);
        Assert.False(await read);
    }

    private static WorkEvent CreateEvent(
        WorkerId? workerId = null,
        WorkDefinitionId? definitionId = null,
        WorkSystemId? workSystemId = null,
        string? workSystemName = null,
        IReadOnlySet<WorkIdentifier>? identifiers = null,
        string eventType = "worker.queued")
        => new(
            DateTimeOffset.UtcNow,
            workSystemId ?? WorkSystemId.New(),
            workSystemName,
            workerId ?? WorkerId.New(),
            definitionId ?? WorkDefinitionId.New(),
            definitionId is { } id ? $"definition-{id.Value:N}" : "definition",
            null,
            null,
            identifiers ?? new HashSet<WorkIdentifier>(),
            eventType,
            null);
}

using System.Reflection;
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
                    state.WorkDefinitionName,
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
                    state.WorkDefinitionName,
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
                    state.WorkDefinitionName,
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
                    state.WorkDefinitionName,
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
    public async Task LazyPublishCreatesOneSharedEventForCursorAndRoutedSubscribers()
    {
        var stream = new WorkEventStream();
        var workerId = WorkerId.New();
        await using var cursorSubscription = stream.Subscribe();
        await using var routedSubscription = stream.Subscribe(new WorkEventFilter(WorkerId: workerId));
        await using var cursorReader = cursorSubscription.Read().GetAsyncEnumerator();
        await using var routedReader = routedSubscription.Read().GetAsyncEnumerator();
        var expected = CreateEvent(workerId: workerId);
        var createCalls = 0;

        stream.Publish(expected, workEvent =>
        {
            createCalls++;
            return workEvent;
        });

        Assert.Same(expected, await ReadNext(cursorReader));
        Assert.Same(expected, await ReadNext(routedReader));
        Assert.Equal(1, createCalls);
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
        var acceptedDefinitionName = $"definition-{acceptedDefinitionId.Value:N}";
        await using var subscription = stream.Subscribe(new WorkEventFilter(DefinitionName: acceptedDefinitionName));
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
    public async Task FiltersByDefinitionNames()
    {
        var stream = new WorkEventStream();
        var acceptedDefinitionId = WorkDefinitionId.New();
        var acceptedDefinitionName = $"definition-{acceptedDefinitionId.Value:N}";
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            DefinitionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { acceptedDefinitionName }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(definitionId: WorkDefinitionId.New(), eventType: "worker.queued");
        var accepted = CreateEvent(definitionId: acceptedDefinitionId, eventType: "worker.queued");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task DefinitionNameSetWithCaseVariantsDeliversOnce()
    {
        var stream = new WorkEventStream();
        const string acceptedDefinitionName = "invoice.close";
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            DefinitionNames: new HashSet<string>
            {
                acceptedDefinitionName,
                acceptedDefinitionName.ToUpperInvariant(),
            }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var accepted = CreateEvent(definitionName: acceptedDefinitionName, eventType: "worker.queued");

        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(1, diagnostics.AcceptedEventCount);
    }

    [Fact]
    public async Task FilterRequiresAllSpecifiedValuesToMatch()
    {
        var stream = new WorkEventStream();
        var acceptedWorkerId = WorkerId.New();
        var acceptedDefinitionId = WorkDefinitionId.New();
        var acceptedDefinitionName = $"definition-{acceptedDefinitionId.Value:N}";
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            WorkerId: acceptedWorkerId,
            DefinitionName: acceptedDefinitionName,
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
    public async Task FiltersBySubject()
    {
        var stream = new WorkEventStream();
        var subject = new WorkSubjectId("invoice", "inv-100");
        await using var subscription = stream.Subscribe(new WorkEventFilter(SubjectId: subject));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(eventType: "worker.queued");
        var accepted = CreateEvent(subjectId: subject, eventType: "worker.started");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task FiltersByConcurrencyKey()
    {
        var stream = new WorkEventStream();
        var key = new WorkConcurrencyKey("account", "acct-100");
        await using var subscription = stream.Subscribe(new WorkEventFilter(ConcurrencyKey: key));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(eventType: "worker.queued");
        var accepted = CreateEvent(concurrencyKey: key, eventType: "worker.started");

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task EventTypeFiltersUseMetadataBeforeCreatingEvents()
    {
        var stream = new WorkEventStream();
        var metadataCreated = false;
        var eventCreated = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(EventType: "worker.completed"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        stream.Publish(
            CreateEvent(eventType: "worker.queued"),
            state =>
            {
                metadataCreated = true;
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.WorkDefinitionName,
                    state.SubjectId,
                    state.ConcurrencyKey,
                    state.EventType);
            },
            state =>
            {
                eventCreated = true;
                return state;
            });
        var accepted = CreateEvent(eventType: "worker.completed");

        stream.Publish(accepted);

        Assert.True(metadataCreated);
        Assert.False(eventCreated);
        Assert.Equal(accepted, await ReadNext(reader));
        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
        Assert.Equal(1, diagnostics.DeliveredEventCount);
    }

    [Fact]
    public async Task StrongAnchorFiltersUseMetadataBeforeCreatingEvents()
    {
        var stream = new WorkEventStream();
        var acceptedSubject = new WorkSubjectId("invoice", "inv-100");
        var metadataCreated = false;
        var eventCreated = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(SubjectId: acceptedSubject));
        stream.Publish(
            CreateEvent(subjectId: acceptedSubject, eventType: "worker.queued"),
            state =>
            {
                metadataCreated = true;
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.WorkDefinitionName,
                    new WorkSubjectId("invoice", "inv-200"),
                    state.ConcurrencyKey,
                    state.EventType);
            },
            state =>
            {
                eventCreated = true;
                return state;
            });

        Assert.True(metadataCreated);
        Assert.False(eventCreated);
        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public async Task DefinitionAnchorFiltersUseMetadataBeforeCreatingEvents()
    {
        var stream = new WorkEventStream();
        var definitionId = WorkDefinitionId.New();
        var metadataCreated = false;
        var eventCreated = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            DefinitionName: $"definition-{definitionId.Value:N}",
            EventType: "worker.completed"));

        stream.Publish(
            CreateEvent(definitionId: definitionId, eventType: "worker.started"),
            state =>
            {
                metadataCreated = true;
                return new WorkEventMetadata(
                    state.WorkSystemId,
                    state.WorkerId,
                    state.WorkDefinitionId,
                    state.WorkDefinitionName,
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
        Assert.False(eventCreated);
        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public async Task LazyPublishWithMetadataFiltersByIdentifierBeforeCreatingEvent()
    {
        var stream = new WorkEventStream();
        var identifier = new WorkIdentifier("invoice", "inv-100");
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(Identifier: identifier));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            null,
            null,
            "worker.started",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { identifier };
            });

        stream.Publish(
            metadata,
            CreateEvent(eventType: "worker.started", identifiers: new HashSet<WorkIdentifier> { identifier }),
            state =>
            {
                created = true;
                return state;
            });

        var workEvent = await ReadNext(reader);

        Assert.True(loadedIdentifiers);
        Assert.True(created);
        Assert.Equal(identifier, Assert.Single(workEvent.Identifiers));
    }

    [Fact]
    public async Task LazyPublishWithMetadataDoesNotCreateEventWhenIdentifierFilterDoesNotMatch()
    {
        var stream = new WorkEventStream();
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(Identifier: new WorkIdentifier("invoice", "inv-100")));
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            null,
            null,
            "worker.started",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { new("invoice", "inv-200") };
            });

        stream.Publish(
            metadata,
            CreateEvent(eventType: "worker.started"),
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
    public async Task IdentifierFilterCanBeCombinedWithOtherFilterValues()
    {
        var stream = new WorkEventStream();
        var identifier = new WorkIdentifier("invoice", "inv-100");
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Identifier: identifier,
            EventType: "worker.completed"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(
            eventType: "worker.started",
            identifiers: new HashSet<WorkIdentifier> { identifier });
        var accepted = CreateEvent(
            eventType: "worker.completed",
            identifiers: new HashSet<WorkIdentifier> { identifier });

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task RoutedAnchorFiltersStillRequireSecondaryValues()
    {
        var stream = new WorkEventStream();
        var workerId = WorkerId.New();
        var identifier = new WorkIdentifier("invoice", "inv-100");
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            WorkerId: workerId,
            Identifier: identifier,
            EventType: "worker.completed"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignoredByIdentifier = CreateEvent(workerId: workerId, eventType: "worker.completed");
        var ignoredByType = CreateEvent(
            workerId: workerId,
            eventType: "worker.started",
            identifiers: new HashSet<WorkIdentifier> { identifier });
        var accepted = CreateEvent(
            workerId: workerId,
            eventType: "worker.completed",
            identifiers: new HashSet<WorkIdentifier> { identifier });

        stream.Publish(ignoredByIdentifier);
        stream.Publish(ignoredByType);
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
    public async Task GenericSubjectKeyFiltersDoNotLoadIdentifiersWhenMetadataMatches()
    {
        var stream = new WorkEventStream();
        var subject = new WorkSubjectId("invoice", "inv-100");
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter>
            {
                new(WorkKeyKind.Subject, subject.Type, subject.Value),
            }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            subject,
            null,
            "worker.queued",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { new("invoice", "inv-200") };
            });

        stream.Publish(
            metadata,
            CreateEvent(subjectId: subject, eventType: "worker.queued"),
            state =>
            {
                created = true;
                return state;
            });

        var workEvent = await ReadNext(reader);

        Assert.False(loadedIdentifiers);
        Assert.True(created);
        Assert.Equal(subject, workEvent.SubjectId);
    }

    [Fact]
    public async Task GenericSubjectKeyFiltersDoNotCreateEventsWhenMetadataDoesNotMatch()
    {
        var stream = new WorkEventStream();
        var loadedIdentifiers = false;
        var created = false;
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter>
            {
                new(WorkKeyKind.Subject, "invoice", "inv-100"),
            }));
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            null,
            new WorkSubjectId("invoice", "inv-200"),
            null,
            "worker.queued",
            () =>
            {
                loadedIdentifiers = true;
                return new HashSet<WorkIdentifier> { new("invoice", "inv-100") };
            });

        stream.Publish(
            metadata,
            CreateEvent(subjectId: new WorkSubjectId("invoice", "inv-200"), eventType: "worker.queued"),
            state =>
            {
                created = true;
                return state;
            });

        Assert.False(loadedIdentifiers);
        Assert.False(created);
        AssertNoQueuedEvents(subscription);
    }

    [Fact]
    public async Task GenericConcurrencyKeyFiltersRoutePublishedEvents()
    {
        var stream = new WorkEventStream();
        var key = new WorkConcurrencyKey("account", "acct-100");
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter>
            {
                new(WorkKeyKind.ConcurrencyKey, key.Type, key.Value),
            }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var ignored = CreateEvent(concurrencyKey: new WorkConcurrencyKey("account", "acct-200"));
        var accepted = CreateEvent(concurrencyKey: key);

        stream.Publish(ignored);
        stream.Publish(accepted);

        Assert.Equal(accepted, await ReadNext(reader));
    }

    [Fact]
    public async Task KeyFiltersMatchingMultipleEventKeysDeliverOnce()
    {
        var stream = new WorkEventStream();
        var key = new WorkEventKeyFilter(null, "invoice", "inv-100");
        var subject = new WorkSubjectId(key.Type, key.Value);
        var identifier = new WorkIdentifier(key.Type, key.Value);
        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter> { key }));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workEvent = CreateEvent(
            subjectId: subject,
            identifiers: new HashSet<WorkIdentifier> { identifier });

        stream.Publish(workEvent);

        Assert.Equal(workEvent, await ReadNext(reader));
        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(1, diagnostics.AcceptedEventCount);
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
    public async Task DisposingCursorSubscriptionCompletesPendingReader()
    {
        var stream = new WorkEventStream();
        var subscription = stream.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();

        Assert.False(read.IsCompleted);

        await subscription.DisposeAsync();

        Assert.False(await ReadCompletion(read));
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposedIdentifierSubscriptionStopsReceivingEventsAndIsRemoved()
    {
        var stream = new WorkEventStream();
        var identifier = new WorkIdentifier("invoice", "inv-100");
        var subscription = stream.Subscribe(new WorkEventFilter(Identifier: identifier));

        Assert.Equal(1, stream.ActiveSubscriptionCount);

        await subscription.DisposeAsync();
        stream.Publish(CreateEvent(eventType: "worker.queued", identifiers: new HashSet<WorkIdentifier> { identifier }));

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
    public async Task DefaultUnfilteredSubscriptionsUseLargerCursorCapacity()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe();

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(8192, diagnostics.Capacity);
    }

    [Fact]
    public async Task ExplicitUnfilteredSubscriptionCapacityIsRespected()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe(options: new WorkEventSubscriptionOptions());

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
    }

    [Fact]
    public async Task EventTypeFilteredSubscriptionsKeepDefaultChannelCapacity()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe(new WorkEventFilter(EventType: "worker.completed"));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
    }

    [Fact]
    public async Task ExplicitBroadFilteredSubscriptionCapacityIsRespected()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe(
            new WorkEventFilter(EventType: "worker.completed"),
            new WorkEventSubscriptionOptions());

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
    }

    [Fact]
    public async Task StrongAnchoredFilteredSubscriptionsKeepDefaultChannelCapacity()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: WorkerId.New()));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
    }

    [Fact]
    public async Task DefinitionFilteredSubscriptionsKeepDefaultChannelCapacity()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe(new WorkEventFilter(DefinitionName: "invoice.close"));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
    }

    [Fact]
    public async Task KeyFilteredSubscriptionsKeepDefaultChannelCapacity()
    {
        var stream = new WorkEventStream();

        await using var subscription = stream.Subscribe(new WorkEventFilter(
            Keys: new HashSet<WorkEventKeyFilter>
            {
                new(WorkKeyKind.Identifier, "invoice", "inv-100"),
            }));

        var diagnostics = AssertNoQueuedEvents(subscription);
        Assert.Equal(256, diagnostics.Capacity);
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
    public async Task EmptyFilterPreservesUnfilteredCursorOverflowDiagnostics()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(
            new WorkEventFilter(),
            new WorkEventSubscriptionOptions(Capacity: 2));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.queued");
        var second = CreateEvent(eventType: "worker.started");
        var third = CreateEvent(eventType: "worker.completed");

        stream.Publish(first);
        stream.Publish(second);
        stream.Publish(third);

        var beforeRead = Assert
            .IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription)
            .GetDiagnosticsSnapshot();
        Assert.Equal(2, beforeRead.QueuedCount);
        Assert.Equal(3, beforeRead.AcceptedEventCount);
        Assert.Equal(1, beforeRead.DroppedEventCount);
        Assert.Equal(second, await ReadNext(reader));
        Assert.Equal(third, await ReadNext(reader));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkSubscriptionAndEnumeratorCancellationTokens(bool cancelSubscriptionToken)
    {
        var stream = new WorkEventStream();
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
    public async Task CursorLogKeepsCapacityPerSubscription()
    {
        var stream = new WorkEventStream();
        await using var small = stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: 1));
        await using var large = stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: 3));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var smallReader = small.Read(cancellation.Token).GetAsyncEnumerator();
        await using var largeReader = large.Read(cancellation.Token).GetAsyncEnumerator();
        var first = CreateEvent(eventType: "worker.queued");
        var second = CreateEvent(eventType: "worker.started");
        var third = CreateEvent(eventType: "worker.completed");

        stream.Publish(first);
        stream.Publish(second);
        stream.Publish(third);

        Assert.Equal(third, await ReadNext(smallReader));
        Assert.Equal(first, await ReadNext(largeReader));
        Assert.Equal(second, await ReadNext(largeReader));
        Assert.Equal(third, await ReadNext(largeReader));
        var smallDiagnostics = AssertNoQueuedEvents(small);
        var largeDiagnostics = AssertNoQueuedEvents(large);
        Assert.Equal(3, smallDiagnostics.AcceptedEventCount);
        Assert.Equal(2, smallDiagnostics.DroppedEventCount);
        Assert.Equal(3, largeDiagnostics.AcceptedEventCount);
        Assert.Equal(0, largeDiagnostics.DroppedEventCount);
    }

    [Fact]
    public async Task ManyCursorReadersDrainPublishedBurst()
    {
        var stream = new WorkEventStream();
        const int subscriptionCount = 64;
        const int eventCount = 100;
        var subscriptions = Enumerable
            .Range(0, subscriptionCount)
            .Select(_ => stream.Subscribe(options: new WorkEventSubscriptionOptions(Capacity: eventCount)))
            .ToArray();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observed = new int[subscriptionCount];
        var readers = subscriptions
            .Select((subscription, index) => Task.Run(async () =>
            {
                await foreach (var _ in subscription.Read(cancellation.Token))
                {
                    if (Interlocked.Increment(ref observed[index]) == eventCount)
                    {
                        return;
                    }
                }
            }, cancellation.Token))
            .ToArray();

        await Task.Yield();
        for (var index = 0; index < eventCount; index++)
        {
            stream.Publish(CreateEvent(eventType: $"worker.{index}"));
        }

        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(observed, count => Assert.Equal(eventCount, count));
        foreach (var subscription in subscriptions)
        {
            var diagnostics = AssertNoQueuedEvents(subscription);
            Assert.Equal(eventCount, diagnostics.AcceptedEventCount);
            Assert.Equal(eventCount, diagnostics.DeliveredEventCount);
            Assert.Equal(0, diagnostics.DroppedEventCount);
            await subscription.DisposeAsync();
        }
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

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.queued"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("observe-queue");
        var workEvent = await ReadNext(reader);

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(handle.WorkerId, workEvent.WorkerId);
        Assert.Equal(definition.Name, workEvent.WorkDefinitionName);
        Assert.Equal("worker.queued", workEvent.EventType);
    }

    [Fact]
    public async Task MakeCleanupIdempotentAndIgnoreLazyPublicationAfterCleanup()
    {
        var stream = new WorkEventStream();
        var subscription = stream.Subscribe();
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            "cleanup.definition",
            null,
            null,
            "worker.completed");
        var created = 0;

        await subscription.DisposeAsync();
        stream.Publish(metadata, 1, _ =>
        {
            created++;
            return CreateEvent(eventType: "worker.completed");
        });
        typeof(WorkEventStream).GetMethod("Remove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(stream, [subscription]);
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        stream.Publish(metadata, 2, _ =>
        {
            created++;
            return CreateEvent(eventType: "worker.completed");
        });

        Assert.Equal(0, created);
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task IgnoreUnmatchedIdentifierAndMalformedKeyFiltersWithoutCreatingLazyEvents()
    {
        var stream = new WorkEventStream();
        var expectedIdentifier = new WorkIdentifier("invoice", "expected");
        await using var identifierSubscription = stream.Subscribe(
            new WorkEventFilter(Identifier: expectedIdentifier),
            new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
        await using var malformedKeySubscription = stream.Subscribe(
            new WorkEventFilter(Keys: new HashSet<WorkEventKeyFilter>
            {
                new(null, "", ""),
            }),
            new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
        var unmatched = CreateEvent(
            eventType: "worker.completed",
            identifiers: new HashSet<WorkIdentifier> { new("invoice", "other") });

        stream.Publish(unmatched);
        var metadata = new WorkEventMetadata(
            unmatched.WorkSystemId,
            unmatched.WorkerId,
            unmatched.WorkDefinitionId,
            unmatched.WorkDefinitionName,
            unmatched.SubjectId,
            unmatched.ConcurrencyKey,
            unmatched.EventType,
            () => unmatched.Identifiers);
        var created = false;
        stream.Publish(metadata, unmatched, state =>
        {
            created = true;
            return state;
        });

        Assert.False(created);
        Assert.Equal(0, AssertNoQueuedEvents(identifierSubscription).AcceptedEventCount);
        Assert.Equal(0, AssertNoQueuedEvents(malformedKeySubscription).AcceptedEventCount);
    }

    [Fact]
    public async Task RoutingAnchorsDistinguishEveryValidAndBlankFilterShape()
    {
        var workerId = WorkerId.New();
        var subject = new WorkSubjectId("invoice", "1");
        var concurrency = new WorkConcurrencyKey("tenant", "2");
        var identifier = new WorkIdentifier("order", "3");
        var definitionScope = new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, "billing.close");
        var blankFilters = new WorkEventFilter[]
        {
            new(),
            new(DefinitionName: " "),
            new(DefinitionNames: new HashSet<string> { " " }),
            new(Keys: new HashSet<WorkEventKeyFilter> { new(null, " ", " ") }),
            new(EventType: " "),
            new(EventTypes: new HashSet<string> { " " }),
        };
        foreach (var filter in blankFilters)
        {
            await using var subscription = streamSubscription(filter);
            Assert.Equal("CursorLog", DeliveryKind(subscription));
        }

        var anchoredFilters = new WorkEventFilter[]
        {
            new(WorkerId: workerId),
            new(DefinitionName: "billing.close"),
            new(DefinitionNames: new HashSet<string> { "billing.close" }),
            new(DefinitionNames: new HashSet<string> { " ", "billing.close" }),
            new(SubjectId: subject),
            new(ConcurrencyKey: concurrency),
            new(Identifier: identifier),
            new(Keys: new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "invoice", "1") }),
            new(EventType: "worker.completed"),
            new(EventTypes: new HashSet<string> { "worker.completed" }),
            new(EventTypes: new HashSet<string> { " ", "worker.completed" }),
            new() { DefinitionKind = WorkEventDefinitionKind.Work },
            new() { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { definitionScope } },
        };
        foreach (var filter in anchoredFilters)
        {
            await using var subscription = streamSubscription(filter);
            Assert.Equal("RoutedChannel", DeliveryKind(subscription));
        }

        IWorkEventSubscription streamSubscription(WorkEventFilter filter)
            => new WorkEventStream().Subscribe(filter);

        static string DeliveryKind(IWorkEventSubscription subscription)
            => subscription.GetType()
                .GetProperty("DeliveryKind", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(subscription)!
                .ToString()!;
    }

    [Fact]
    public async Task MetadataPredicateRequiresEveryConfiguredFilterDimension()
    {
        var stream = new WorkEventStream();
        var workerId = WorkerId.New();
        var subject = new WorkSubjectId("invoice", "1");
        var concurrency = new WorkConcurrencyKey("tenant", "2");
        var identifier = new WorkIdentifier("order", "3");
        var scope = new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, "billing.close");
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            workerId,
            WorkDefinitionId.New(),
            "billing.close",
            subject,
            concurrency,
            "worker.completed",
            () => new HashSet<WorkIdentifier> { identifier });
        var complete = new WorkEventFilter(
            workerId,
            "BILLING.CLOSE",
            new HashSet<string> { "billing.close" },
            subject,
            concurrency,
            identifier,
            new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "invoice", "1") },
            "WORKER.COMPLETED",
            new HashSet<string> { "worker.completed" })
        {
            DefinitionKind = WorkEventDefinitionKind.Work,
            AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { scope },
        };
        await using var matching = stream.Subscribe(
            complete,
            new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
        Assert.True(ShouldPublish(matching, metadata));

        var mismatches = new WorkEventFilter[]
        {
            complete with { DefinitionKind = WorkEventDefinitionKind.Workflow },
            complete with { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { new(WorkEventDefinitionKind.Work, "other") } },
            complete with { WorkerId = WorkerId.New() },
            complete with { DefinitionName = "other" },
            complete with { DefinitionNames = new HashSet<string> { "other" } },
            complete with { SubjectId = new WorkSubjectId("invoice", "other") },
            complete with { ConcurrencyKey = new WorkConcurrencyKey("tenant", "other") },
            complete with { Identifier = new WorkIdentifier("order", "other") },
            complete with { Keys = new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "invoice", "other") } },
            complete with { EventType = "worker.failed" },
            complete with { EventTypes = new HashSet<string> { "worker.failed" } },
        };
        foreach (var filter in mismatches)
        {
            await using var subscription = stream.Subscribe(
                filter,
                new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
            Assert.False(ShouldPublish(subscription, metadata));
        }

        var independentlyMatchingFilters = new WorkEventFilter[]
        {
            new() { DefinitionKind = WorkEventDefinitionKind.Work },
            new() { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { scope } },
            new(WorkerId: workerId),
            new(DefinitionName: "BILLING.CLOSE"),
            new(DefinitionNames: new HashSet<string> { "billing.close" }),
            new(SubjectId: subject),
            new(ConcurrencyKey: concurrency),
            new(Identifier: identifier),
            new(Keys: new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "invoice", "1") }),
            new(EventType: "WORKER.COMPLETED"),
            new(EventTypes: new HashSet<string> { "worker.completed" }),
        };
        foreach (var filter in independentlyMatchingFilters)
        {
            await using var subscription = stream.Subscribe(
                filter,
                new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
            Assert.True(ShouldPublish(subscription, metadata));
        }

        await using var namedSubscription = stream.Subscribe(
            new WorkEventFilter(DefinitionNames: new HashSet<string> { "billing.close" }),
            new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
        var unnamedMetadata = new WorkEventMetadata(
            metadata.WorkSystemId,
            metadata.WorkerId,
            metadata.DefinitionId,
            definitionName: null,
            metadata.SubjectId,
            metadata.ConcurrencyKey,
            metadata.EventType,
            () => new HashSet<WorkIdentifier> { identifier });
        Assert.False(ShouldPublish(namedSubscription, unnamedMetadata));

        var sparseMetadata = new WorkEventMetadata(
            metadata.WorkSystemId,
            workerId: null,
            definitionId: null,
            definitionName: null,
            subjectId: null,
            concurrencyKey: null,
            metadata.EventType);
        var sparseMismatches = new WorkEventFilter[]
        {
            new() { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { scope } },
            new(WorkerId: workerId),
            new(SubjectId: subject),
            new(ConcurrencyKey: concurrency),
            new(Identifier: identifier),
            new(Keys: new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "invoice", "1") }),
        };
        foreach (var filter in sparseMismatches)
        {
            await using var subscription = stream.Subscribe(
                filter,
                new WorkEventSubscriptionOptions(OverflowBehavior: WorkEventOverflowBehavior.DropWrite));
            Assert.False(ShouldPublish(subscription, sparseMetadata));
        }

        await using var full = stream.Subscribe(
            new WorkEventFilter { DefinitionKind = WorkEventDefinitionKind.Work },
            new WorkEventSubscriptionOptions(1, WorkEventOverflowBehavior.DropWrite));
        Assert.True(ShouldPublish(full, metadata));
        stream.Publish(CreateEvent(eventType: metadata.EventType));
        Assert.False(ShouldPublish(full, metadata));

        await matching.DisposeAsync();
        Assert.False(ShouldPublish(matching, metadata));

        static bool ShouldPublish(IWorkEventSubscription subscription, WorkEventMetadata metadata)
            => (bool)subscription.GetType()
                .GetMethod("ShouldPublish", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(subscription, [metadata])!;
    }

    [Fact]
    public async Task LazyMetadataPublishingSharesOneEventAcrossIndexedAndScannedSubscribers()
    {
        await using var stream = new WorkEventStream();
        var workerId = WorkerId.New();
        var systemId = WorkSystemId.New();
        var definitionId = WorkDefinitionId.New();
        var workEvent = CreateEvent(
            workerId,
            definitionId,
            "billing.close",
            systemId,
            eventType: "worker.completed");
        var metadata = new WorkEventMetadata(
            systemId,
            workerId,
            definitionId,
            "billing.close",
            subjectId: null,
            concurrencyKey: null,
            "worker.completed");
        await using var scanned = stream.Subscribe(new WorkEventFilter
        {
            DefinitionKind = WorkEventDefinitionKind.Work,
        });
        await using var scannedReader = scanned.Read().GetAsyncEnumerator();
        var createCount = 0;

        stream.Publish(metadata, workEvent, state =>
        {
            createCount++;
            return state;
        });
        Assert.Same(workEvent, await ReadNext(scannedReader));
        Assert.Equal(1, createCount);

        await using var indexed = stream.Subscribe(new WorkEventFilter(WorkerId: workerId));
        stream.Publish(metadata, workEvent, state =>
        {
            createCount++;
            return state;
        });
        await using var indexedReader = indexed.Read().GetAsyncEnumerator();
        Assert.Same(workEvent, await ReadNext(scannedReader));
        Assert.Same(workEvent, await ReadNext(indexedReader));
        Assert.Equal(2, createCount);
    }

    [Fact]
    public async Task EmptySubscriptionIndexAndUnfilteredAdmissionHonorCapacity()
    {
        var indexType = typeof(WorkEventStream).GetNestedType("SubscriptionIndex", BindingFlags.NonPublic)!;
        var subscriptionType = typeof(WorkEventStream).GetNestedType("WorkEventSubscription", BindingFlags.NonPublic)!;
        var emptySubscriptions = Array.CreateInstance(subscriptionType, 0);
        Assert.NotNull(indexType.GetMethod("Create")!.Invoke(null, [emptySubscriptions]));

        await using var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(
            filter: null,
            new WorkEventSubscriptionOptions(1, WorkEventOverflowBehavior.DropWrite));
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            "capacity.test",
            subjectId: null,
            concurrencyKey: null,
            "worker.completed");
        var shouldPublish = subscription.GetType().GetMethod(
            "ShouldPublish",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(Assert.IsType<bool>(shouldPublish.Invoke(subscription, [metadata])));
        stream.Publish(CreateEvent(eventType: "worker.completed"));
        Assert.False(Assert.IsType<bool>(shouldPublish.Invoke(subscription, [metadata])));
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

    [Fact]
    public async Task UnfilteredSubscriptionExposesNoRoutingAnchors()
    {
        var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe();
        var type = subscription.GetType();

        foreach (var propertyName in new[]
        {
            "WorkerIdFilter",
            "SubjectIdFilter",
            "ConcurrencyKeyFilter",
            "IdentifierFilter",
            "KeyFilters",
            "DefinitionNameFilter",
            "DefinitionNamesFilter",
            "EventTypeFilter",
            "EventTypesFilter",
        })
        {
            Assert.Null(type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(subscription));
        }
    }

    [Fact]
    public async Task EventLogCoversEmptyBoundsFilteringCapacityAndCompletion()
    {
        var type = typeof(WorkEventStream).GetNestedType("EventLog", BindingFlags.NonPublic)!;
        var log = Activator.CreateInstance(type, nonPublic: true)!;
        object?[] emptyRead = [1L, null];
        Assert.False((bool)type.GetMethod("TryRead")!.Invoke(log, emptyRead)!);
        Assert.Null(emptyRead[1]);
        Assert.Equal(0, type.GetMethod("CountMatching")!.Invoke(log, [1L, 2L, null, 0]));
        Assert.Equal(0, type.GetMethod("CountMatching")!.Invoke(log, [2L, 1L, null, 10]));
        Assert.Equal(0, type.GetMethod("CountMatching")!.Invoke(log, [1L, 2L, null, 10]));

        var first = CreateEvent(eventType: "worker.first");
        var second = CreateEvent(eventType: "worker.second");
        type.GetMethod("Append")!.Invoke(log, [first, 10]);
        type.GetMethod("Append")!.Invoke(log, [second, 10]);
        object?[] beforeFirst = [0L, null];
        object?[] afterLast = [3L, null];
        object?[] retained = [1L, null];
        Assert.False((bool)type.GetMethod("TryRead")!.Invoke(log, beforeFirst)!);
        Assert.False((bool)type.GetMethod("TryRead")!.Invoke(log, afterLast)!);
        Assert.True((bool)type.GetMethod("TryRead")!.Invoke(log, retained)!);
        Assert.Same(first, retained[1]);
        Assert.Equal(2, type.GetMethod("CountMatching")!.Invoke(log, [1L, 2L, null, 10]));
        Assert.Equal(1, type.GetMethod("CountMatching")!.Invoke(log, [1L, 1L, null, 10]));
        Assert.Equal(1, type.GetMethod("CountMatching")!.Invoke(log, [1L, 2L, null, 1]));
        Assert.Equal(1, type.GetMethod("CountMatching")!.Invoke(
            log,
            [1L, 2L, new WorkEventFilter(EventType: "worker.second"), 10]));
        Assert.Equal(0, type.GetMethod("CountMatching")!.Invoke(log, [3L, 4L, null, 10]));

        var waitForKnownTail = (Task)type.GetMethod("WaitForAdvance")!.Invoke(
            log,
            [0L, CancellationToken.None])!;
        await waitForKnownTail;
        type.GetMethod("Complete")!.Invoke(log, null);
        type.GetMethod("Complete")!.Invoke(log, null);
        type.GetMethod("Append")!.Invoke(log, [CreateEvent(eventType: "worker.late"), 10]);
        var waitAfterCompletion = (Task)type.GetMethod("WaitForAdvance")!.Invoke(
            log,
            [2L, CancellationToken.None])!;
        await waitAfterCompletion;
    }

    [Fact]
    public async Task CursorSubscriptionWithOnlyBlankSetValuesSkipsPublishedEvents()
    {
        await using var stream = new WorkEventStream();
        await using var subscription = stream.Subscribe(
            new WorkEventFilter(DefinitionNames: new HashSet<string> { " " }),
            new WorkEventSubscriptionOptions(2, WorkEventOverflowBehavior.DropOldest));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();

        stream.Publish(CreateEvent(definitionName: "orders.submit"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        var diagnostics = Assert.IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription)
            .GetDiagnosticsSnapshot();
        Assert.Equal(0, diagnostics.DeliveredEventCount);
        Assert.Equal(0, diagnostics.QueuedCount);
    }

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
        string? definitionName = null,
        WorkSystemId? workSystemId = null,
        string? workSystemName = null,
        WorkSubjectId? subjectId = null,
        WorkConcurrencyKey? concurrencyKey = null,
        IReadOnlySet<WorkIdentifier>? identifiers = null,
        string eventType = "worker.queued")
        => new(
            DateTimeOffset.UtcNow,
            workSystemId ?? WorkSystemId.New(),
            workSystemName,
            workerId ?? WorkerId.New(),
            definitionId ?? WorkDefinitionId.New(),
            definitionName ?? (definitionId is { } id ? $"definition-{id.Value:N}" : "definition"),
            subjectId,
            concurrencyKey,
            identifiers ?? new HashSet<WorkIdentifier>(),
            eventType,
            null);
}

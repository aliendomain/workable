using Workable;
using System.Text;
using System.Text.Json;

namespace Workable.Tests;

[Trait("Category", "Events")]
public sealed class WorkEventFilterShould
{
    [Fact]
    public void RequireEveryConfiguredEventDimensionWithoutBroadeningEmptySets()
    {
        var workerId = WorkerId.New();
        var subject = new WorkSubjectId("order", "100");
        var concurrency = new WorkConcurrencyKey("account", "200");
        var identifier = new WorkIdentifier("customer", "300");
        var identifiers = new HashSet<WorkIdentifier> { identifier };
        var workEvent = CreateEvent(workerId, "orders.run", subject, concurrency, identifiers);

        Assert.True(new WorkEventFilter().Matches(workEvent));
        Assert.True(new WorkEventFilter(DefinitionNames: new HashSet<string>()).Matches(workEvent));
        Assert.True(new WorkEventFilter(EventTypes: new HashSet<string>()).Matches(workEvent));
        Assert.True(new WorkEventFilter(Keys: new HashSet<WorkEventKeyFilter>()).Matches(workEvent));
        Assert.True((new WorkEventFilter { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope>() }).Matches(workEvent));

        Assert.True(new WorkEventFilter(WorkerId: workerId).Matches(workEvent));
        Assert.False(new WorkEventFilter(WorkerId: WorkerId.New()).Matches(workEvent));
        Assert.True(new WorkEventFilter(DefinitionName: "ORDERS.RUN").Matches(workEvent));
        Assert.False(new WorkEventFilter(DefinitionName: "other").Matches(workEvent));
        Assert.True(new WorkEventFilter(DefinitionNames: new HashSet<string> { "ORDERS.RUN" }).Matches(workEvent));
        Assert.False(new WorkEventFilter(DefinitionNames: new HashSet<string> { "other" }).Matches(workEvent));
        Assert.False(new WorkEventFilter(DefinitionNames: new HashSet<string> { "orders.run" }).Matches(
            CreateEvent(workerId, definitionName: null, subject, concurrency, identifiers)));
        Assert.True(new WorkEventFilter(SubjectId: subject).Matches(workEvent));
        Assert.False(new WorkEventFilter(SubjectId: new WorkSubjectId("order", "other")).Matches(workEvent));
        Assert.True(new WorkEventFilter(ConcurrencyKey: concurrency).Matches(workEvent));
        Assert.False(new WorkEventFilter(ConcurrencyKey: new WorkConcurrencyKey("account", "other")).Matches(workEvent));
        Assert.True(new WorkEventFilter(Identifier: identifier).Matches(workEvent));
        Assert.False(new WorkEventFilter(Identifier: new WorkIdentifier("customer", "other")).Matches(workEvent));
        Assert.True(new WorkEventFilter(EventType: "WORKER.QUEUED").Matches(workEvent));
        Assert.False(new WorkEventFilter(EventType: "worker.failed").Matches(workEvent));
        Assert.True(new WorkEventFilter(EventTypes: new HashSet<string> { "WORKER.QUEUED" }).Matches(workEvent));
        Assert.False(new WorkEventFilter(EventTypes: new HashSet<string> { "worker.failed" }).Matches(workEvent));
        Assert.True((new WorkEventFilter { DefinitionKind = WorkEventDefinitionKind.Work }).Matches(workEvent));
        Assert.False((new WorkEventFilter { DefinitionKind = WorkEventDefinitionKind.Workflow }).Matches(workEvent));

        var scope = new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, "orders.run");
        Assert.True((new WorkEventFilter { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { scope } }).Matches(workEvent));
        Assert.False((new WorkEventFilter
        {
            AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope>
            {
                new(WorkEventDefinitionKind.Work, "other"),
            },
        }).Matches(workEvent));
        Assert.False((new WorkEventFilter { AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { scope } }).Matches(
            CreateEvent(workerId, definitionName: null, subject, concurrency, identifiers)));

        var completeFilter = new WorkEventFilter(
            WorkerId: workerId,
            DefinitionName: "ORDERS.RUN",
            DefinitionNames: new HashSet<string> { "orders.run" },
            SubjectId: subject,
            ConcurrencyKey: concurrency,
            Identifier: identifier,
            Keys: new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "order", "100") },
            EventType: "WORKER.QUEUED",
            EventTypes: new HashSet<string> { "worker.queued" })
        {
            DefinitionKind = WorkEventDefinitionKind.Work,
            AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { scope },
        };
        Assert.True(completeFilter.Matches(workEvent));

        var sparseEvent = new WorkEvent(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            "events",
            workerId: null,
            workDefinitionId: null,
            workDefinitionName: "orders.run",
            subjectId: null,
            concurrencyKey: null,
            identifiers: new HashSet<WorkIdentifier>(),
            eventType: "worker.queued",
            data: null);
        Assert.False(new WorkEventFilter(WorkerId: workerId).Matches(sparseEvent));
        Assert.False(new WorkEventFilter(SubjectId: subject).Matches(sparseEvent));
        Assert.False(new WorkEventFilter(ConcurrencyKey: concurrency).Matches(sparseEvent));
    }

    [Fact]
    public void MatchRelationshipKeysByExplicitOrAnySupportedKind()
    {
        var subject = new WorkSubjectId("order", "100");
        var concurrency = new WorkConcurrencyKey("account", "200");
        var identifier = new WorkIdentifier("customer", "300");
        var identifiers = new HashSet<WorkIdentifier> { identifier };

        Assert.False(new WorkEventKeyFilter(WorkKeyKind.Subject, "", "100").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(WorkKeyKind.Subject, "order", " ").Matches(subject, concurrency, identifiers));
        Assert.True(new WorkEventKeyFilter(WorkKeyKind.Subject, "order", "100").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(WorkKeyKind.Subject, "ORDER", "100").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(WorkKeyKind.Subject, "order", "other").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(WorkKeyKind.Subject, "order", "100").Matches(null, concurrency, identifiers));

        Assert.True(new WorkEventKeyFilter(WorkKeyKind.ConcurrencyKey, "account", "200").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(WorkKeyKind.ConcurrencyKey, "account", "200").Matches(subject, null, identifiers));
        Assert.True(new WorkEventKeyFilter(WorkKeyKind.Identifier, "customer", "300").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(WorkKeyKind.Identifier, "customer", "other").Matches(subject, concurrency, identifiers));

        Assert.True(new WorkEventKeyFilter(null, "order", "100").Matches(subject, concurrency, identifiers));
        Assert.True(new WorkEventKeyFilter(null, "account", "200").Matches(subject, concurrency, identifiers));
        Assert.True(new WorkEventKeyFilter(null, "customer", "300").Matches(subject, concurrency, identifiers));
        Assert.False(new WorkEventKeyFilter(null, "missing", "value").Matches(subject, concurrency, identifiers));
    }

    [Fact]
    public void MetadataMatchesAnyKeyAcrossBlankMissingExplicitAndWildcardSelectors()
    {
        var calls = 0;
        var subject = new WorkSubjectId("order", "100");
        var concurrency = new WorkConcurrencyKey("account", "200");
        var identifier = new WorkIdentifier("customer", "300");
        var metadata = new WorkEventMetadata(
            WorkSystemId.New(),
            WorkerId.New(),
            WorkDefinitionId.New(),
            "orders.run",
            subject,
            concurrency,
            "worker.queued",
            () =>
            {
                calls++;
                return new HashSet<WorkIdentifier> { identifier };
            });

        Assert.True(metadata.ContainsAnyKey(null));
        Assert.True(metadata.ContainsAnyKey(new HashSet<WorkEventKeyFilter>()));
        Assert.Equal(0, calls);
        Assert.False(metadata.ContainsAnyKey(new HashSet<WorkEventKeyFilter>
        {
            new(null, " ", "100"),
            new(null, "order", " "),
            new(WorkKeyKind.Subject, "order", "missing"),
            new(WorkKeyKind.ConcurrencyKey, "account", "missing"),
            new(WorkKeyKind.Identifier, "customer", "missing"),
        }));
        Assert.Equal(1, calls);
        Assert.True(metadata.ContainsAnyKey(new HashSet<WorkEventKeyFilter> { new(null, "order", "100") }));
        Assert.True(metadata.ContainsAnyKey(new HashSet<WorkEventKeyFilter> { new(null, "account", "200") }));
        Assert.True(metadata.ContainsAnyKey(new HashSet<WorkEventKeyFilter> { new(null, "customer", "300") }));
        Assert.True(metadata.ContainsAnyKey(new HashSet<WorkEventKeyFilter>
        {
            new(WorkKeyKind.Subject, "order", "100"),
        }));
        Assert.True(metadata.ContainsIdentifier(identifier));
        Assert.Equal(1, calls);

        var withoutKeys = new WorkEventMetadata(
            WorkSystemId.New(),
            null,
            null,
            definitionName: null,
            subjectId: null,
            concurrencyKey: null,
            eventType: "system.changed");
        Assert.Null(withoutKeys.DefinitionScope);
        Assert.False(withoutKeys.ContainsAnyKey(new HashSet<WorkEventKeyFilter>
        {
            new(null, "order", "100"),
        }));
    }

    [Fact]
    public void DeserializeOnlyConcreteEventPayloadsAndHandleNullIdentifierSets()
    {
        var identifiers = new HashSet<WorkIdentifier>();
        var withoutData = CreateEvent(WorkerId.New(), "orders.run", new("order", "1"), new("account", "2"), identifiers);
        var undefinedData = CreateEventWithData(default(JsonElement));
        using var nullDocument = JsonDocument.Parse("null");
        var explicitNullData = CreateEventWithData(nullDocument.RootElement.Clone());
        using var valueDocument = JsonDocument.Parse("{\"VALUE\":42}");
        var valueData = CreateEventWithData(valueDocument.RootElement.Clone());

        Assert.Null(withoutData.DeserializeData<Payload>());
        Assert.Null(undefinedData.DeserializeData<Payload>());
        Assert.Null(explicitNullData.DeserializeData<Payload>());
        Assert.Equal(42, valueData.DeserializeData<Payload>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!.Value);

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("null"));
        reader.Read();
        var converter = new WorkEventIdentifierSetJsonConverter();
        Assert.Empty(converter.Read(ref reader, typeof(IReadOnlySet<WorkIdentifier>), WorkEventJson.Options));
    }

    private static WorkEvent CreateEventWithData(JsonElement data)
        => new(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            "events",
            WorkerId.New(),
            WorkDefinitionId.New(),
            "orders.run",
            null,
            null,
            new HashSet<WorkIdentifier>(),
            "worker.queued",
            data);

    private static WorkEvent CreateEvent(
        WorkerId workerId,
        string? definitionName,
        WorkSubjectId subject,
        WorkConcurrencyKey concurrency,
        IReadOnlySet<WorkIdentifier> identifiers)
        => new(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            "events",
            workerId,
            WorkDefinitionId.New(),
            definitionName,
            subject,
            concurrency,
            identifiers,
            "worker.queued",
            data: null);

    private sealed record Payload(int Value);
}

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkEventStreamShould
{
    [Fact]
    public void ReturnEmptySubscriptionWithoutCallingInnerWhenNoDefinitionsAreReadable()
    {
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups(), out var inner, hidden);

        var subscription = stream.Subscribe(new WorkEventFilter(EventType: "worker.queued"));

        Assert.Empty(inner.Subscriptions);
        var diagnostics = Assert.IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription);
        Assert.Equal(0, diagnostics.GetDiagnosticsSnapshot().Capacity);
    }

    [Fact]
    public void ReturnEmptySubscriptionWithoutCallingInnerForUnreadableDefinitionFilter()
    {
        var visible = CreateDefinition("visible.events", "visible.read");
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups("visible.read"), out var inner, visible, hidden);

        var subscription = stream.Subscribe(new WorkEventFilter(DefinitionName: hidden.Name));

        Assert.Empty(inner.Subscriptions);
        var diagnostics = Assert.IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription);
        Assert.Equal(0, diagnostics.GetDiagnosticsSnapshot().Capacity);
    }

    [Fact]
    public void ForwardReadableDefinitionFilterWithTypedAuthorizationScope()
    {
        var visible = CreateDefinition("visible.events", "visible.read");
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups("visible.read"), out var inner, visible, hidden);
        var filter = new WorkEventFilter(DefinitionName: visible.Name, EventType: "worker.completed");
        var options = new WorkEventSubscriptionOptions(Capacity: 12);

        stream.Subscribe(filter, options);

        var subscription = Assert.Single(inner.Subscriptions);
        Assert.NotSame(filter, subscription.Filter);
        Assert.Equal(filter.DefinitionName, subscription.Filter?.DefinitionName);
        Assert.Contains(
            new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, visible.Name),
            subscription.Filter?.AuthorizedDefinitions ?? new HashSet<WorkEventDefinitionScope>());
        Assert.Same(options, subscription.Options);
    }

    [Fact]
    public void RestrictDefinitionSetFilterToReadableDefinitions()
    {
        var visible = CreateDefinition("visible.events", "visible.read");
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups("visible.read"), out var inner, visible, hidden);
        var filter = new WorkEventFilter(
            DefinitionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { visible.Name, hidden.Name },
            EventType: "worker.failed");

        stream.Subscribe(filter);

        var forwarded = Assert.Single(inner.Subscriptions).Filter;
        Assert.NotNull(forwarded);
        Assert.Equal("worker.failed", forwarded.EventType);
        Assert.Equal([visible.Name], forwarded.DefinitionNames);
    }

    [Fact]
    public void AddReadableDefinitionScopeWhenFilterDoesNotSpecifyDefinitions()
    {
        var first = CreateDefinition("first.events", "first.read");
        var second = CreateDefinition("second.events", "second.read");
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups("first.read", "second.read"), out var inner, first, second, hidden);
        var filter = new WorkEventFilter(WorkerId: WorkerId.New(), EventType: "worker.log");

        stream.Subscribe(filter);

        var forwarded = Assert.Single(inner.Subscriptions).Filter;
        Assert.NotNull(forwarded);
        Assert.NotSame(filter, forwarded);
        Assert.Equal(filter.WorkerId, forwarded.WorkerId);
        Assert.Equal(filter.EventType, forwarded.EventType);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Name, second.Name },
            forwarded.DefinitionNames);
    }

    [Theory]
    [InlineData(WorkEventDefinitionKind.Work)]
    [InlineData(WorkEventDefinitionKind.Workflow)]
    public async Task KeepSameNamedWorkAndWorkflowEventAuthorizationSeparate(
        WorkEventDefinitionKind readableKind)
    {
        const string sharedName = "shared.events";
        await using var inner = new WorkEventStream();
        var stream = new AuthorizedWorkEventStream(
            inner,
            readableKind == WorkEventDefinitionKind.Work
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sharedName }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            readableKind == WorkEventDefinitionKind.Workflow
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sharedName }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        await using var subscription = stream.Subscribe(new WorkEventFilter(DefinitionName: sharedName));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        inner.Publish(CreateEvent(sharedName, Opposite(readableKind)));
        inner.Publish(CreateEvent(sharedName, readableKind));

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(readableKind, reader.Current.DefinitionKind);
    }

    [Fact]
    public async Task KeepSameNamedCatalogDefinitionsSeparateInAuthorizedSessions()
    {
        const string sharedName = "shared.catalog.events";
        var work = WorkDefinition.Create(
            sharedName,
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: ["work.read"],
                operateGroups: ["work.operate"]));
        var workflow = WorkflowDefinition.Create(
            sharedName,
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: ["workflow.read"],
                operateGroups: ["workflow.operate"]));
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(true);
            builder.AddWork(work, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(workflow, definition => definition.DispatchWork("dispatch", work));
        });

        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var workSession = await system.CreateSession(Context("work-reader", "work.read", "work.operate"));
        var workflowSession = await system.CreateSession(Context("workflow-reader", "workflow.read"));
        Assert.Equal([work.Id], workSession.Catalog.Definitions.Select(static definition => definition.Id));
        Assert.Empty(workflowSession.Catalog.Definitions);
        await using var workSubscription = workSession.Events.Subscribe(new WorkEventFilter(DefinitionName: sharedName));
        await using var workflowSubscription = workflowSession.Events.Subscribe(new WorkEventFilter(DefinitionName: sharedName));
        await using var workReader = workSubscription.Read().GetAsyncEnumerator();
        await using var workflowReader = workflowSubscription.Read().GetAsyncEnumerator();
        var queued = await workSession.Queue.Enqueue(sharedName);
        var started = await Assert.IsType<InMemoryWorkSystem>(system).WorkflowRuntime.Start(
            sharedName,
            Context("workflow-operator", "workflow.operate"));

        Assert.True(queued.QueueOutcome.IsAccepted);
        Assert.True(started.StartOutcome.IsAccepted);

        Assert.True(await workReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(WorkEventDefinitionKind.Work, workReader.Current.DefinitionKind);
        Assert.True(await workflowReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(WorkEventDefinitionKind.Workflow, workflowReader.Current.DefinitionKind);
    }

    private static WorkEvent CreateEvent(string definitionName, WorkEventDefinitionKind definitionKind)
        => new(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            workSystemName: null,
            workerId: definitionKind == WorkEventDefinitionKind.Work ? WorkerId.New() : null,
            workDefinitionId: definitionKind == WorkEventDefinitionKind.Work ? WorkDefinitionId.New() : null,
            definitionName,
            subjectId: null,
            concurrencyKey: null,
            identifiers: new HashSet<WorkIdentifier>(),
            eventType: definitionKind == WorkEventDefinitionKind.Work ? "worker.completed" : "workflow.completed",
            data: null,
            definitionKind,
            workflowDefinitionId: definitionKind == WorkEventDefinitionKind.Workflow ? WorkflowDefinitionId.New() : null);

    private static WorkEventDefinitionKind Opposite(WorkEventDefinitionKind kind)
        => kind == WorkEventDefinitionKind.Work
            ? WorkEventDefinitionKind.Workflow
            : WorkEventDefinitionKind.Work;

    private static WorkRequestContext Context(string actorId, params string[] groups)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(actorId),
            isAuthenticated: true) with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName: null,
                new WorkActor(actorId),
                groups,
                readableDefinitionIds: null),
        };

    private static AuthorizedWorkEventStream CreateStream(
        IReadOnlySet<string> groups,
        out RecordingWorkEventStream inner,
        params WorkDefinition[] definitions)
    {
        inner = new RecordingWorkEventStream();
        return new AuthorizedWorkEventStream(
            inner,
            definitions
                .Where(definition => definition.Authorization.CanRead(groups, false))
                .Select(definition => definition.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static WorkDefinition CreateDefinition(string name, params string[] readGroups)
        => WorkDefinition.Create(
            name,
            authorization: WorkDefinitionAuthorization.Create(readGroups: readGroups));

    private static HashSet<string> Groups(params string[] groups)
        => new(groups, StringComparer.OrdinalIgnoreCase);

    private sealed record RecordedSubscription(
        WorkEventFilter? Filter,
        WorkEventSubscriptionOptions? Options);

    private sealed class RecordingWorkEventStream : IWorkEventStream
    {
        public List<RecordedSubscription> Subscriptions { get; } = [];

        public IWorkEventSubscription Subscribe(
            WorkEventFilter? filter = null,
            WorkEventSubscriptionOptions? options = null)
        {
            this.Subscriptions.Add(new RecordedSubscription(filter, options));
            return new RecordingWorkEventSubscription();
        }
    }

    private sealed class RecordingWorkEventSubscription : IWorkEventSubscription
    {
        public IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default)
            => Empty(cancellationToken);

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<WorkEvent> Empty(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}

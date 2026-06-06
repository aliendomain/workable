using System.Runtime.CompilerServices;
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

        var subscription = stream.Subscribe(new WorkEventFilter(DefinitionId: hidden.Id));

        Assert.Empty(inner.Subscriptions);
        var diagnostics = Assert.IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription);
        Assert.Equal(0, diagnostics.GetDiagnosticsSnapshot().Capacity);
    }

    [Fact]
    public void ForwardReadableDefinitionFilterUnchanged()
    {
        var visible = CreateDefinition("visible.events", "visible.read");
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups("visible.read"), out var inner, visible, hidden);
        var filter = new WorkEventFilter(DefinitionId: visible.Id, EventType: "worker.completed");
        var options = new WorkEventSubscriptionOptions(Capacity: 12);

        stream.Subscribe(filter, options);

        var subscription = Assert.Single(inner.Subscriptions);
        Assert.Same(filter, subscription.Filter);
        Assert.Same(options, subscription.Options);
    }

    [Fact]
    public void RestrictDefinitionSetFilterToReadableDefinitions()
    {
        var visible = CreateDefinition("visible.events", "visible.read");
        var hidden = CreateDefinition("hidden.events", "hidden.read");
        var stream = CreateStream(Groups("visible.read"), out var inner, visible, hidden);
        var filter = new WorkEventFilter(
            DefinitionIds: new HashSet<WorkDefinitionId> { visible.Id, hidden.Id },
            EventType: "worker.failed");

        stream.Subscribe(filter);

        var forwarded = Assert.Single(inner.Subscriptions).Filter;
        Assert.NotNull(forwarded);
        Assert.Equal("worker.failed", forwarded.EventType);
        Assert.Equal([visible.Id], forwarded.DefinitionIds);
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
        Assert.Equal(new HashSet<WorkDefinitionId> { first.Id, second.Id }, forwarded.DefinitionIds);
    }

    private static AuthorizedWorkEventStream CreateStream(
        IReadOnlySet<string> groups,
        out RecordingWorkEventStream inner,
        params WorkDefinition[] definitions)
    {
        var catalog = new WorkSystemCatalog(
            definitions.Select(definition => new RegisteredWork(definition, _ => new NoopExecutor(), [])).ToArray(),
            persistenceStoreAvailable: false);
        inner = new RecordingWorkEventStream();
        return new AuthorizedWorkEventStream(
            inner,
            new WorkAuthorizationEvaluator(catalog, groups, false));
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

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}

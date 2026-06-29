namespace Workable;

internal sealed class AuthorizedWorkChangeStream(
    IWorkChangeStream inner,
    WorkAuthorizationEvaluator authorization,
    bool canViewDiagnostics) : IWorkChangeStream
{
    public IWorkChangeSubscription Subscribe(WorkChangeSubscriptionOptions? options = null)
    {
        var hasReadAllWorkAccess = authorization.HasReadAllWorkAccess();
        var readableDefinitionNames = authorization.ReadableDefinitions()
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (readableDefinitionNames.Count == 0 && !canViewDiagnostics)
        {
            return EmptyWorkChangeSubscription.Instance;
        }

        return new AuthorizedWorkChangeSubscription(
            inner.Subscribe(options),
            readableDefinitionNames,
            canViewDiagnostics,
            hasReadAllWorkAccess || canViewDiagnostics);
    }

    private sealed class AuthorizedWorkChangeSubscription(
        IWorkChangeSubscription inner,
        IReadOnlySet<string> readableDefinitionNames,
        bool canViewDiagnostics,
        bool exposeDiagnostics) : IWorkChangeSubscription, IWorkChangeSubscriptionDiagnostics
    {
        public IAsyncEnumerable<WorkChange> Read(CancellationToken cancellationToken = default)
            => this.Filter(inner.Read(cancellationToken), cancellationToken);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();

        public WorkChangeSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot()
            => exposeDiagnostics && inner is IWorkChangeSubscriptionDiagnostics diagnostics
                ? diagnostics.GetDiagnosticsSnapshot()
                : EmptyWorkChangeSubscription.EmptyDiagnostics;

        private async IAsyncEnumerable<WorkChange> Filter(
            IAsyncEnumerable<WorkChange> changes,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var change in changes.WithCancellation(cancellationToken))
            {
                if (this.CanRead(change.Key))
                {
                    yield return change;
                }
            }
        }

        private bool CanRead(WorkChangeKey key)
            => key.Kind switch
            {
                WorkChangeKind.Diagnostics => canViewDiagnostics,
                WorkChangeKind.Definition => readableDefinitionNames.Contains(key.Value),
                WorkChangeKind.System => readableDefinitionNames.Count > 0 || canViewDiagnostics,
                WorkChangeKind.Worker or
                WorkChangeKind.Subject or
                WorkChangeKind.ConcurrencyKey or
                WorkChangeKind.Identifier => readableDefinitionNames.Count > 0,
                _ => false,
            };
    }
}

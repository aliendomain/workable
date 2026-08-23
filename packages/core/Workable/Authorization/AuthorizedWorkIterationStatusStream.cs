namespace Workable;

internal sealed class AuthorizedWorkIterationStatusStream(
    WorkIterationStatusStream inner,
    IReadOnlySet<string> readableDefinitionNames,
    bool canViewDiagnostics) : IWorkIterationStatusStream
{
    public IWorkIterationStatusSubscription Subscribe(
        WorkerIterationReference iteration,
        long afterSequence = 0)
    {
        if (!inner.TryGetDefinitionName(iteration, out var definitionName) ||
            !readableDefinitionNames.Contains(definitionName))
        {
            return EmptyWorkIterationStatusSubscription.Instance;
        }

        return new AuthorizedWorkIterationStatusSubscription(
            inner.Subscribe(iteration, afterSequence),
            canViewDiagnostics);
    }

    private sealed class AuthorizedWorkIterationStatusSubscription(
        IWorkIterationStatusSubscription inner,
        bool canViewDiagnostics) : IWorkIterationStatusSubscription
    {
        public WorkIterationStatusCompletion? Completion
        {
            get
            {
                var completion = inner.Completion;
                return completion is null || canViewDiagnostics
                    ? completion
                    : completion with
                    {
                        Iteration = WorkProfileAccessFilter.Apply(
                            completion.Iteration,
                            canViewDiagnostics: false),
                    };
            }
        }

        public IAsyncEnumerable<WorkIterationStatusItem> Read(CancellationToken cancellationToken = default)
            => inner.Read(cancellationToken);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
    }
}

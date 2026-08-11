namespace Workable;

internal static class ChildWorkQueueContext
{
    private static readonly AsyncLocal<State?> Ambient = new();

    internal static IChildWorkQueueService Current
        => Ambient.Value?.Queue ?? UnavailableChildWorkQueueService.Instance;

    internal static IDisposable Begin(IChildWorkQueueService queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (ReferenceEquals(Current, queue))
        {
            return EmptyScope.Instance;
        }

        var prior = Ambient.Value;
        Ambient.Value = new State(queue);
        return new Scope(prior);
    }

    private sealed record State(IChildWorkQueueService Queue);

    private sealed class Scope(State? prior) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                Ambient.Value = prior;
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

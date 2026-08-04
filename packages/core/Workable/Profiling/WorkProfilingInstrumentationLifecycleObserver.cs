namespace Workable;

internal sealed class WorkProfilingInstrumentationLifecycleObserver(
    IEnumerable<IWorkProfilingInstrumentationFactory> factories,
    IWorkProfilingContextAccessor profilingContextAccessor) : IWorkSystemLifecycleObserver, IDisposable
{
    private readonly object gate = new();
    private readonly IReadOnlyList<IWorkProfilingInstrumentationFactory> factories = [.. factories];
    private readonly Dictionary<WorkSystemId, IReadOnlyList<IDisposable>> instrumentation = [];
    private bool disposed;

    public Task SystemStarted(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.instrumentation.ContainsKey(system.Id))
            {
                return Task.CompletedTask;
            }

            var created = new List<IDisposable>(this.factories.Count);
            try
            {
                foreach (var factory in this.factories)
                {
                    created.Add(factory.Create(system.Id, profilingContextAccessor));
                }

                this.instrumentation.Add(system.Id, created);
            }
            catch
            {
                DisposeAll(created);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task SystemStopping(
        IWorkSystem system,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SystemStopped(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<IDisposable>? stopped = null;
        lock (this.gate)
        {
            if (this.instrumentation.Remove(system.Id, out var current))
            {
                stopped = current;
            }
        }

        DisposeAll(stopped);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IReadOnlyList<IDisposable> current;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            current = [.. this.instrumentation.Values.SelectMany(static observers => observers)];
            this.instrumentation.Clear();
        }

        DisposeAll(current);
    }

    private static void DisposeAll(IEnumerable<IDisposable>? instrumentation)
    {
        if (instrumentation is null)
        {
            return;
        }

        foreach (var item in instrumentation)
        {
            item.Dispose();
        }
    }
}

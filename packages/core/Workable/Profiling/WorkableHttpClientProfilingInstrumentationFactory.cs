namespace Workable;

internal sealed class WorkableHttpClientProfilingInstrumentationFactory :
    IWorkProfilingInstrumentationFactory,
    IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<WorkSystemId, int> registrations = [];
    private readonly HashSet<WorkSystemId> stoppingSystems = [];
    private WorkableHttpClientProfilingObserver? observer;
    private IWorkProfilingContextAccessor? profilingContextAccessor;
    private bool disposed;

    internal WorkableHttpClientProfilingObserver? Observer
    {
        get
        {
            lock (this.gate)
            {
                return this.observer;
            }
        }
    }

    public IDisposable Create(
        WorkSystemId systemId,
        IWorkProfilingContextAccessor profilingContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(profilingContextAccessor);

        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            while (this.stoppingSystems.Contains(systemId))
            {
                Monitor.Wait(this.gate);
                ObjectDisposedException.ThrowIf(this.disposed, this);
            }

            if (this.profilingContextAccessor is not null &&
                !ReferenceEquals(this.profilingContextAccessor, profilingContextAccessor))
            {
                throw new InvalidOperationException(
                    "HTTP client profiling registrations must share one profiling context accessor.");
            }

            this.profilingContextAccessor = profilingContextAccessor;
            this.observer ??= new WorkableHttpClientProfilingObserver(profilingContextAccessor);
            if (this.registrations.TryGetValue(systemId, out var count))
            {
                this.registrations[systemId] = count + 1;
            }
            else
            {
                this.registrations.Add(systemId, 1);
                this.observer.RegisterSystem(systemId);
            }

            return new Registration(this, systemId);
        }
    }

    public void Dispose()
    {
        WorkableHttpClientProfilingObserver? current;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.registrations.Clear();
            current = this.observer;
            this.observer = null;
        }

        current?.Dispose();
    }

    private void Release(WorkSystemId systemId)
    {
        WorkableHttpClientProfilingObserver? current = null;
        WorkableHttpClientProfilingObserver? stopped = null;
        lock (this.gate)
        {
            if (!this.registrations.TryGetValue(systemId, out var count))
            {
                return;
            }

            if (count > 1)
            {
                this.registrations[systemId] = count - 1;
                return;
            }

            this.registrations.Remove(systemId);
            current = this.observer;
            this.stoppingSystems.Add(systemId);
            if (this.registrations.Count == 0)
            {
                stopped = this.observer;
                this.observer = null;
            }
        }

        try
        {
            current?.UnregisterSystem(systemId);
        }
        finally
        {
            lock (this.gate)
            {
                this.stoppingSystems.Remove(systemId);
                Monitor.PulseAll(this.gate);
            }

            stopped?.Dispose();
        }
    }

    private sealed class Registration(
        WorkableHttpClientProfilingInstrumentationFactory owner,
        WorkSystemId systemId) : IDisposable
    {
        private WorkableHttpClientProfilingInstrumentationFactory? owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref this.owner, null)?.Release(systemId);
    }
}

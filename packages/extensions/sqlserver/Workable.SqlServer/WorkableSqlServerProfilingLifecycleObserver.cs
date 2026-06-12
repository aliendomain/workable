using System.Collections.Concurrent;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerProfilingLifecycleObserver(
    IWorkProfilingContextAccessor profilingContextAccessor) : IWorkSystemLifecycleObserver, IDisposable
{
    private readonly ConcurrentDictionary<WorkSystemId, WorkableSqlServerCommandProfilingObserver> observers = new();
    private bool disposed;

    public Task SystemStarted(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        cancellationToken.ThrowIfCancellationRequested();
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(WorkableSqlServerProfilingLifecycleObserver));
        }

        if (this.observers.ContainsKey(system.Id))
        {
            return Task.CompletedTask;
        }

        var observer = new WorkableSqlServerCommandProfilingObserver(system.Id, profilingContextAccessor);
        if (!this.observers.TryAdd(system.Id, observer))
        {
            observer.Dispose();
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

        if (this.observers.TryRemove(system.Id, out var observer))
        {
            observer.Dispose();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        foreach (var systemId in this.observers.Keys.ToArray())
        {
            if (this.observers.TryRemove(systemId, out var observer))
            {
                observer.Dispose();
            }
        }
    }
}

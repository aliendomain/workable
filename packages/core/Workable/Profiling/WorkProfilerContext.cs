using System.Threading;

namespace Workable;

internal sealed class WorkProfilerContext : IDisposable
{
    private static readonly AsyncLocal<IWorkProfiler?> CurrentProfiler = new();
    private readonly IWorkProfiler? previous;
    private bool disposed;

    private WorkProfilerContext(IWorkProfiler? profiler)
    {
        this.previous = CurrentProfiler.Value;
        CurrentProfiler.Value = profiler;
    }

    public static IWorkProfiler? Current => CurrentProfiler.Value;

    public static IDisposable Begin(IWorkProfiler? profiler)
        => new WorkProfilerContext(profiler);

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        CurrentProfiler.Value = this.previous;
    }
}

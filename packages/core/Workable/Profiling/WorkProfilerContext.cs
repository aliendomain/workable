using System.Threading;

namespace Workable;

internal sealed class WorkProfilerContext : IDisposable
{
    private static readonly AsyncLocal<ProfilerExecutionContext?> CurrentProfiler = new();
    private readonly ProfilerExecutionContext? previous;
    private bool disposed;

    private WorkProfilerContext(WorkSystemId? systemId, IWorkProfiler? profiler)
    {
        this.previous = CurrentProfiler.Value;
        CurrentProfiler.Value = profiler is null
            ? null
            : new ProfilerExecutionContext(systemId, profiler);
    }

    public static IWorkProfiler? Current => CurrentProfiler.Value?.Profiler;

    public static IDisposable Begin(IWorkProfiler? profiler)
        => new WorkProfilerContext(systemId: null, profiler);

    public static IDisposable Begin(WorkSystemId systemId, IWorkProfiler? profiler)
        => new WorkProfilerContext(systemId, profiler);

    public static bool TryGetCurrent(out WorkProfilingContext context)
    {
        if (CurrentProfiler.Value is not { SystemId: { } systemId, Profiler: { } profiler })
        {
            context = default;
            return false;
        }

        context = new WorkProfilingContext(systemId, profiler);
        return true;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        CurrentProfiler.Value = this.previous;
    }

    private sealed record ProfilerExecutionContext(WorkSystemId? SystemId, IWorkProfiler Profiler);
}

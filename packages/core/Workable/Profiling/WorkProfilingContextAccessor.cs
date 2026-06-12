namespace Workable;

internal sealed class WorkProfilingContextAccessor : IWorkProfilingContextAccessor
{
    public bool TryGetCurrent(out WorkProfilingContext context)
        => WorkProfilerContext.TryGetCurrent(out context);
}

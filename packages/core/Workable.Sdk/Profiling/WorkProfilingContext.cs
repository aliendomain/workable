namespace Workable;

/// <summary>
/// Describes the currently active worker profiling context for the executing async flow.
/// </summary>
/// <param name="SystemId">The owning Workable system for the active worker execution.</param>
/// <param name="Profiler">The profiler receiving profile nodes for the active worker execution.</param>
public readonly record struct WorkProfilingContext(
    WorkSystemId SystemId,
    IWorkProfiler Profiler)
{
    /// <summary>
    /// Attempts to add an informational node produced by automatic instrumentation.
    /// </summary>
    public bool TryAddAutomaticInfo(string instrumentation, string name, object? context = null)
    {
        if (this.Profiler is IWorkAutomaticProfiler automaticProfiler)
        {
            return automaticProfiler.TryAddAutomaticInfo(instrumentation, name, context);
        }

        this.Profiler.AddInfo(name, context);
        return true;
    }

    /// <summary>
    /// Attempts to add an informational node produced by automatic instrumentation, creating its
    /// context only when the active profile admits the node.
    /// </summary>
    public bool TryAddAutomaticInfo<TContext>(
        string instrumentation,
        string name,
        Func<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        if (this.Profiler is IWorkAutomaticProfiler automaticProfiler)
        {
            return automaticProfiler.TryAddAutomaticInfo(instrumentation, name, contextFactory);
        }

        this.Profiler.AddInfo(name, contextFactory());
        return true;
    }

    /// <summary>
    /// Attempts to start a timing node produced by automatic instrumentation.
    /// </summary>
    public bool TryStartAutomaticTiming(
        string instrumentation,
        string name,
        object? context,
        out IWorkProfileScope? scope)
    {
        if (this.Profiler is IWorkAutomaticProfiler automaticProfiler)
        {
            return automaticProfiler.TryStartAutomaticTiming(instrumentation, name, context, out scope);
        }

        scope = this.Profiler.StartTiming(name, context);
        return true;
    }

    /// <summary>
    /// Attempts to start a timing node produced by automatic instrumentation, creating its context
    /// only when the active profile admits the node.
    /// </summary>
    public bool TryStartAutomaticTiming<TContext>(
        string instrumentation,
        string name,
        Func<TContext> contextFactory,
        out TContext? context,
        out IWorkProfileScope? scope)
        where TContext : class
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        if (this.Profiler is IWorkAutomaticProfiler automaticProfiler)
        {
            return automaticProfiler.TryStartAutomaticTiming(
                instrumentation,
                name,
                contextFactory,
                out context,
                out scope);
        }

        context = contextFactory();
        scope = this.Profiler.StartTiming(name, context);
        return true;
    }
}

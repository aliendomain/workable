namespace Workable;

/// <summary>
/// Describes the currently active worker profiling context for the executing async flow.
/// </summary>
/// <param name="SystemId">The owning Workable system for the active worker execution.</param>
/// <param name="Profiler">The profiler receiving profile nodes for the active worker execution.</param>
public readonly record struct WorkProfilingContext(
    WorkSystemId SystemId,
    IWorkProfiler Profiler);

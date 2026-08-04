namespace Workable;

/// <summary>
/// Creates system-scoped instrumentation that contributes automatic timing and diagnostic nodes
/// to active worker profiles.
/// </summary>
/// <remarks>
/// <see cref="Create"/> is called once for each started Workable system, and its returned handle is
/// disposed when that system stops. The handle may own dedicated instrumentation or lease shared
/// instrumentation. Implementations should use the supplied profiling context accessor to ensure
/// that an observed operation belongs to a registered system before adding profile nodes.
/// </remarks>
public interface IWorkProfilingInstrumentationFactory
{
    /// <summary>
    /// Creates instrumentation for one started Workable system.
    /// </summary>
    /// <param name="systemId">The system that owns profile nodes captured by the instrumentation.</param>
    /// <param name="profilingContextAccessor">The accessor used to resolve the active worker profile.</param>
    /// <returns>A handle that stops or releases the system's instrumentation registration when disposed.</returns>
    IDisposable Create(
        WorkSystemId systemId,
        IWorkProfilingContextAccessor profilingContextAccessor);
}

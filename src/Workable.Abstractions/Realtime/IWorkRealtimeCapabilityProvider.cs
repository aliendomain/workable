namespace Workable;

/// <summary>
/// Reports whether realtime features are available for the current host.
/// </summary>
public interface IWorkRealtimeCapabilityProvider
{
    /// <summary>
    /// Gets the current realtime capability description.
    /// </summary>
    /// <returns>The realtime capability available to callers.</returns>
    WorkRealtimeCapability GetCapability();
}

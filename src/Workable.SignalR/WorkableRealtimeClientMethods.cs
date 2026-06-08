namespace Workable;

/// <summary>
/// Defines the SignalR client method names used by the Workable realtime hub.
/// </summary>
public static class WorkableRealtimeClientMethods
{
    /// <summary>
    /// The client method used when a raw subscription delivers exactly one event.
    /// </summary>
    public const string WorkEvent = "workable.event";

    /// <summary>
    /// The client method used when a raw subscription delivers a batch of events.
    /// </summary>
    public const string WorkEvents = "workable.events";

    /// <summary>
    /// The client method used for named view updates.
    /// </summary>
    public const string ViewUpdated = "workable.view";

    /// <summary>
    /// The client method used for worker-overview updates.
    /// </summary>
    public const string WorkerOverviewUpdated = "workable.workerOverview";
}

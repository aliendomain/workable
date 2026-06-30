namespace Workable;

/// <summary>
/// Captures queueing and delivery diagnostics for one event subscription.
/// </summary>
/// <param name="Capacity">The configured maximum delivery capacity.</param>
/// <param name="OverflowBehavior">The overflow behavior applied when bounded delivery reaches capacity.</param>
/// <param name="QueuedCount">The current number of events buffered or retained for the subscription but not yet delivered.</param>
/// <param name="PeakQueuedCount">The highest queued or retained event count observed for the subscription.</param>
/// <param name="AcceptedEventCount">The total number of events accepted by the subscription.</param>
/// <param name="DeliveredEventCount">The total number of events delivered to readers.</param>
/// <param name="DroppedEventCount">The total number of events dropped because of overflow handling.</param>
public sealed record WorkEventSubscriptionDiagnosticsSnapshot(
    int Capacity,
    WorkEventOverflowBehavior OverflowBehavior,
    int QueuedCount,
    int PeakQueuedCount,
    long AcceptedEventCount,
    long DeliveredEventCount,
    long DroppedEventCount);

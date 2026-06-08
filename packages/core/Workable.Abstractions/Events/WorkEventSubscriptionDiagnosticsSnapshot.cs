namespace Workable;

/// <summary>
/// Captures queueing and delivery diagnostics for one event subscription buffer.
/// </summary>
/// <param name="Capacity">The configured maximum buffer capacity.</param>
/// <param name="OverflowBehavior">The overflow behavior applied when the buffer reaches capacity.</param>
/// <param name="QueuedCount">The current number of events buffered but not yet delivered.</param>
/// <param name="PeakQueuedCount">The highest queued event count observed for the subscription.</param>
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

namespace Workable;

/// <summary>
/// Captures queueing and coalescing diagnostics for one change subscription.
/// </summary>
/// <param name="Capacity">The configured maximum number of pending distinct change keys.</param>
/// <param name="QueuedCount">The current number of pending distinct change keys.</param>
/// <param name="PeakQueuedCount">The highest pending distinct change-key count observed for the subscription.</param>
/// <param name="AcceptedChangeCount">The total number of changes accepted by the subscription.</param>
/// <param name="DeliveredChangeCount">The total number of changes delivered to readers.</param>
/// <param name="CoalescedChangeCount">The total number of accepted changes that updated an already-pending key.</param>
/// <param name="DroppedChangeCount">The total number of pending changes dropped because of capacity pressure.</param>
public sealed record WorkChangeSubscriptionDiagnosticsSnapshot(
    int Capacity,
    int QueuedCount,
    int PeakQueuedCount,
    long AcceptedChangeCount,
    long DeliveredChangeCount,
    long CoalescedChangeCount,
    long DroppedChangeCount);

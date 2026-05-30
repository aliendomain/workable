namespace Workable;

public sealed record WorkEventSubscriptionDiagnosticsSnapshot(
    int Capacity,
    WorkEventOverflowBehavior OverflowBehavior,
    int QueuedCount,
    int PeakQueuedCount,
    long AcceptedEventCount,
    long DeliveredEventCount,
    long DroppedEventCount);

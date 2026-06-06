namespace Workable;

public sealed record WorkableRealtimeDebugEventSubscriptionSnapshot(
    string ConnectionId,
    string GroupName,
    WorkEventFilter? Filter,
    int GroupConnectionCount,
    bool IsStreaming);

public sealed record WorkableRealtimeDebugViewSubscriptionSnapshot(
    string ConnectionId,
    string SubscriptionId,
    string ViewName,
    string GroupName,
    WorkViewCriteria Criteria,
    long InitialReadModelSequence,
    int GroupConnectionCount);

public sealed record WorkableRealtimeDebugWorkerOverviewSubscriptionSnapshot(
    string ConnectionId,
    string SubscriptionId,
    WorkerId WorkerId,
    string GroupName,
    WorkWorkerOverviewRealtimeCriteria Criteria,
    int GroupConnectionCount,
    bool IsStreaming,
    DateTimeOffset? LastActivityAt,
    string? LastError,
    DateTimeOffset? StreamingStartedAt,
    DateTimeOffset? StreamingStoppedAt,
    WorkEventSubscriptionDiagnosticsSnapshot? EventStreamDiagnostics);

public sealed record WorkableRealtimeDebugSystemSnapshot(
    string? SystemName,
    string? SystemState,
    IReadOnlyList<WorkableRealtimeDebugEventSubscriptionSnapshot> EventSubscriptions,
    IReadOnlyList<WorkableRealtimeDebugViewSubscriptionSnapshot> ViewSubscriptions,
    IReadOnlyList<WorkableRealtimeDebugWorkerOverviewSubscriptionSnapshot> WorkerOverviewSubscriptions);

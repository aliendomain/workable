namespace Workable;

/// <summary>
/// Describes one active raw event subscription as exposed by the local realtime debug routes.
/// </summary>
/// <param name="ConnectionId">The SignalR connection id that owns the subscription.</param>
/// <param name="GroupName">The normalized SignalR group used to share the subscription.</param>
/// <param name="Filter">The normalized raw event filter for the subscription.</param>
/// <param name="GroupConnectionCount">The number of connections currently sharing the same group.</param>
/// <param name="IsStreaming">Indicates whether the shared group currently has an active event pump.</param>
public sealed record WorkableRealtimeDebugEventSubscriptionSnapshot(
    string ConnectionId,
    string GroupName,
    WorkEventFilter? Filter,
    int GroupConnectionCount,
    bool IsStreaming);

/// <summary>
/// Describes one active named-view subscription as exposed by the local realtime debug routes.
/// </summary>
/// <param name="ConnectionId">The SignalR connection id that owns the subscription.</param>
/// <param name="SubscriptionId">The caller-supplied logical subscription id.</param>
/// <param name="ViewName">The normalized view name.</param>
/// <param name="GroupName">The normalized SignalR group used to share the subscription.</param>
/// <param name="Criteria">The normalized view criteria for the subscription.</param>
/// <param name="InitialReadModelSequence">The read-model sequence observed when the subscription was created.</param>
/// <param name="GroupConnectionCount">The number of connections currently sharing the same group.</param>
public sealed record WorkableRealtimeDebugViewSubscriptionSnapshot(
    string ConnectionId,
    string SubscriptionId,
    string ViewName,
    string GroupName,
    WorkViewCriteria Criteria,
    long InitialReadModelSequence,
    int GroupConnectionCount);

/// <summary>
/// Describes one active worker-overview subscription as exposed by the local realtime debug routes.
/// </summary>
/// <param name="ConnectionId">The SignalR connection id that owns the subscription.</param>
/// <param name="SubscriptionId">The caller-supplied logical subscription id.</param>
/// <param name="WorkerId">The worker currently being observed.</param>
/// <param name="GroupName">The normalized SignalR group used to share the subscription.</param>
/// <param name="Criteria">The normalized realtime criteria for the worker-overview subscription.</param>
/// <param name="GroupConnectionCount">The number of connections currently sharing the same group.</param>
/// <param name="IsStreaming">Indicates whether the shared group currently has an active realtime pump.</param>
/// <param name="LastActivityAt">The most recent source activity seen by the shared group.</param>
/// <param name="LastError">The last observed streaming error, if any.</param>
/// <param name="StreamingStartedAt">The time the current or most recent streaming session started.</param>
/// <param name="StreamingStoppedAt">The time the most recent streaming session stopped.</param>
/// <param name="ChangeStreamDiagnostics">Optional queue/channel diagnostics for the underlying change stream.</param>
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
    WorkChangeSubscriptionDiagnosticsSnapshot? ChangeStreamDiagnostics);

/// <summary>
/// Describes the active realtime subscriptions for one Workable system.
/// </summary>
/// <param name="SystemName">The named Workable system, or <see langword="null"/> for the default system.</param>
/// <param name="SystemState">The current system state string.</param>
/// <param name="EventSubscriptions">The active raw event subscriptions.</param>
/// <param name="ViewSubscriptions">The active named-view subscriptions.</param>
/// <param name="WorkerOverviewSubscriptions">The active worker-overview subscriptions.</param>
public sealed record WorkableRealtimeDebugSystemSnapshot(
    string? SystemName,
    string? SystemState,
    IReadOnlyList<WorkableRealtimeDebugEventSubscriptionSnapshot> EventSubscriptions,
    IReadOnlyList<WorkableRealtimeDebugViewSubscriptionSnapshot> ViewSubscriptions,
    IReadOnlyList<WorkableRealtimeDebugWorkerOverviewSubscriptionSnapshot> WorkerOverviewSubscriptions);

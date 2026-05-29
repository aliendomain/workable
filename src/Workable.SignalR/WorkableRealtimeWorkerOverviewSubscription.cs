namespace Workable;

internal sealed record WorkableRealtimeWorkerOverviewSubscription(
    string ConnectionId,
    string SubscriptionId,
    WorkSystemId SystemId,
    WorkerId WorkerId,
    WorkWorkerOverviewRealtimeCriteria Criteria,
    string GroupName,
    WorkAuthorizationSnapshot Authorization);

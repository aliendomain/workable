namespace Workable;

internal sealed record WorkableRealtimeViewSubscription(
    string ConnectionId,
    string SubscriptionId,
    WorkSystemId SystemId,
    string ViewName,
    WorkViewCriteria Criteria,
    string GroupName,
    long InitialReadModelSequence,
    long InitialWorkflowSequence,
    WorkAuthorizationSnapshot Authorization);

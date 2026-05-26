namespace Workable;

internal sealed record WorkableRealtimeViewSubscription(
    string SubscriptionId,
    WorkSystemId SystemId,
    string ViewName,
    WorkViewCriteria Criteria,
    string GroupName,
    long InitialReadModelSequence,
    WorkAuthorizationSnapshot Authorization);

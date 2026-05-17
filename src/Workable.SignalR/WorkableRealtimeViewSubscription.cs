namespace Workable;

internal sealed record WorkableRealtimeViewSubscription(
    WorkSystemId SystemId,
    string ViewName,
    WorkViewCriteria Criteria,
    string GroupName,
    long InitialReadModelSequence);

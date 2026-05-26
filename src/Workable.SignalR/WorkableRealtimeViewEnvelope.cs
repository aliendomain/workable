namespace Workable;

public sealed record WorkableRealtimeViewEnvelope<T>(
    string SubscriptionId,
    string ViewName,
    T Result);

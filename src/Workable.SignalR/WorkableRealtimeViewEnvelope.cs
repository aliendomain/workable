namespace Workable;

/// <summary>
/// Wraps a realtime SignalR payload with the caller's subscription id and normalized view name.
/// </summary>
/// <typeparam name="T">The result payload type.</typeparam>
/// <param name="SubscriptionId">The logical subscription handle supplied by the client.</param>
/// <param name="ViewName">The normalized view name associated with the payload.</param>
/// <param name="Result">The realtime payload delivered for the subscription.</param>
public sealed record WorkableRealtimeViewEnvelope<T>(
    string SubscriptionId,
    string ViewName,
    T Result);

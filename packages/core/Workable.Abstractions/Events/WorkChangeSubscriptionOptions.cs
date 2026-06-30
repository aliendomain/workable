namespace Workable;

/// <summary>
/// Configures bounded delivery for a coalesced change subscription.
/// </summary>
/// <param name="Capacity">The maximum number of pending distinct change keys retained for the subscription.</param>
public sealed record WorkChangeSubscriptionOptions(int Capacity = 1024);

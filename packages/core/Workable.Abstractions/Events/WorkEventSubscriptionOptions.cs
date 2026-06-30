namespace Workable;
/// <summary>
/// Configures buffering behavior for a work event subscription.
/// </summary>
/// <param name="Capacity">The maximum number of queued or retained source events for the subscription.</param>
/// <param name="OverflowBehavior">The behavior to apply when bounded delivery reaches capacity.</param>
public sealed record WorkEventSubscriptionOptions(
    int Capacity = 256,
    WorkEventOverflowBehavior OverflowBehavior = WorkEventOverflowBehavior.DropOldest);

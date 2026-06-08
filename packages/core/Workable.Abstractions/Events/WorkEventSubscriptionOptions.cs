namespace Workable;
/// <summary>
/// Configures buffering behavior for a work event subscription.
/// </summary>
/// <param name="Capacity">The maximum number of queued events the subscription buffer should retain.</param>
/// <param name="OverflowBehavior">The behavior to apply when the subscription buffer reaches capacity.</param>
public sealed record WorkEventSubscriptionOptions(
    int Capacity = 256,
    WorkEventOverflowBehavior OverflowBehavior = WorkEventOverflowBehavior.DropOldest);

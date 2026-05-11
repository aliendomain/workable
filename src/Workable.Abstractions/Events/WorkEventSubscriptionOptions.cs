using System.Diagnostics.CodeAnalysis;

namespace Workable;
public sealed record WorkEventSubscriptionOptions(
    int Capacity = 256,
    WorkEventOverflowBehavior OverflowBehavior = WorkEventOverflowBehavior.DropOldest);

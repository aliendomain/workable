using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkConcurrencyConfiguration
{
    public static WorkConcurrencyConfiguration Default { get; } = new();

    public bool IsEnabled { get; init; }

    public int MaximumCapacity { get; init; }

    public WorkConcurrencyScope Scope { get; init; } = WorkConcurrencyScope.PerDefinition;

    public WorkConcurrencyBlockingMode BlockingMode { get; init; } = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed;

    public WorkConcurrencyLimitReachedBehavior LimitReachedBehavior { get; init; } = WorkConcurrencyLimitReachedBehavior.Ignore;

    public WorkConcurrencyOverrideBehavior OverrideBehavior { get; init; } = WorkConcurrencyOverrideBehavior.Flexible;
}

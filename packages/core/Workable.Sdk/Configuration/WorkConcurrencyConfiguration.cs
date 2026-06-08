using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures concurrency coordination for a work definition or worker instance.
/// </summary>
/// <remarks>
/// Concurrency is enabled only when both this configuration and the parent
/// <see cref="WorkCoordinationConfiguration"/> are enabled.
/// </remarks>
public sealed record WorkConcurrencyConfiguration
{
    /// <summary>
    /// Gets the default concurrency configuration with coordination disabled.
    /// </summary>
    public static WorkConcurrencyConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether concurrency coordination is enabled for the work definition.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the maximum number of workers that can hold capacity in the selected scope.
    /// </summary>
    public int MaximumCapacity { get; init; }

    /// <summary>
    /// Gets the grouping used to decide which workers compete for the same capacity.
    /// </summary>
    public WorkConcurrencyScope Scope { get; init; } = WorkConcurrencyScope.PerDefinition;

    /// <summary>
    /// Gets the worker states that continue holding concurrency capacity.
    /// </summary>
    public WorkConcurrencyBlockingMode BlockingMode { get; init; } = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed;

    /// <summary>
    /// Gets the behavior used when queueing reaches the configured capacity limit.
    /// </summary>
    public WorkConcurrencyLimitReachedBehavior LimitReachedBehavior { get; init; } = WorkConcurrencyLimitReachedBehavior.Ignore;

    /// <summary>
    /// Gets the behavior used when a manual start attempts to bypass capacity rules.
    /// </summary>
    public WorkConcurrencyOverrideBehavior OverrideBehavior { get; init; } = WorkConcurrencyOverrideBehavior.Flexible;
}

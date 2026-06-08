using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Declares default concurrency coordination for a work executor.
/// </summary>
/// <remarks>
/// <para>
/// Workable reads this attribute from the executor type during registration and merges the resulting
/// configuration into the work definition before any fluent configuration callbacks run.
/// </para>
/// <para>
/// Use fluent configuration when a host needs to override or supplement the attribute for a specific registration.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkConcurrencyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkConcurrencyAttribute"/> class.
    /// </summary>
    /// <param name="isEnabled">Enables concurrency coordination for the work definition.</param>
    /// <param name="maximumCapacity">Maximum concurrent capacity for the selected scope.</param>
    /// <param name="scope">Grouping used when calculating shared capacity.</param>
    /// <param name="blockingMode">Worker states that continue holding concurrency capacity.</param>
    /// <param name="limitReachedBehavior">Behavior used when queueing reaches the configured capacity limit.</param>
    /// <param name="overrideBehavior">Behavior used when a manual start attempts to bypass capacity checks.</param>
    public WorkConcurrencyAttribute(
        bool isEnabled = false,
        int maximumCapacity = 0,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerDefinition,
        WorkConcurrencyBlockingMode blockingMode = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior = WorkConcurrencyLimitReachedBehavior.Ignore,
        WorkConcurrencyOverrideBehavior overrideBehavior = WorkConcurrencyOverrideBehavior.Flexible)
    {
        this.Configuration = new WorkConcurrencyConfiguration
        {
            IsEnabled = isEnabled,
            MaximumCapacity = maximumCapacity,
            Scope = scope,
            BlockingMode = blockingMode,
            LimitReachedBehavior = limitReachedBehavior,
            OverrideBehavior = overrideBehavior,
        };

        var validationConfiguration = WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = isEnabled,
                Concurrency = this.Configuration,
            },
        };
        WorkConfigurationValidator.ThrowIfInvalid(validationConfiguration);
    }

    /// <summary>
    /// Gets the validated concurrency configuration produced by the attribute.
    /// </summary>
    public WorkConcurrencyConfiguration Configuration { get; }
}

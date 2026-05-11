using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkConcurrencyAttribute : Attribute
{
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

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Concurrency = this.Configuration });
    }

    public WorkConcurrencyConfiguration Configuration { get; }
}

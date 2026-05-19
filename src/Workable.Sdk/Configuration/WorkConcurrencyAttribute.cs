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

    public WorkConcurrencyConfiguration Configuration { get; }
}

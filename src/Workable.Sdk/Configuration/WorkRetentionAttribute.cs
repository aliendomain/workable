using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Declares default final-worker retention settings for a work executor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkRetentionAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRetentionAttribute"/> class.
    /// </summary>
    /// <param name="purgeIntervalSeconds">The purge interval, in seconds, for completed or canceled workers.</param>
    /// <param name="maximumFinalWorkers">
    /// The target number of completed or canceled workers retained for the definition.
    /// </param>
    public WorkRetentionAttribute(
        int purgeIntervalSeconds = 600,
        int maximumFinalWorkers = 1_000)
    {
        this.Configuration = new WorkRetentionConfiguration
        {
            PurgeInterval = TimeSpan.FromSeconds(purgeIntervalSeconds),
            MaximumFinalWorkers = maximumFinalWorkers,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Retention = this.Configuration });
    }

    /// <summary>
    /// Gets the validated retention configuration produced by the attribute.
    /// </summary>
    public WorkRetentionConfiguration Configuration { get; }
}

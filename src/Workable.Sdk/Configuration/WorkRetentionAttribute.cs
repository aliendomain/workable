using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkRetentionAttribute : Attribute
{
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

    public WorkRetentionConfiguration Configuration { get; }
}

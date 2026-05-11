using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkRetentionAttribute : Attribute
{
    public WorkRetentionAttribute(int purgeIntervalSeconds = 300)
    {
        this.Configuration = new WorkRetentionConfiguration
        {
            PurgeInterval = TimeSpan.FromSeconds(purgeIntervalSeconds),
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Retention = this.Configuration });
    }

    public WorkRetentionConfiguration Configuration { get; }
}

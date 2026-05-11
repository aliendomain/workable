using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkLoggingAttribute : Attribute
{
    public WorkLoggingAttribute(
        bool isEnabled = true,
        LogLevel level = LogLevel.Information,
        int maximumBufferedEntries = 100)
    {
        this.Configuration = new WorkLoggingConfiguration
        {
            IsEnabled = isEnabled,
            Level = level,
            MaximumBufferedEntries = maximumBufferedEntries,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Logging = this.Configuration });
    }

    public WorkLoggingConfiguration Configuration { get; }
}

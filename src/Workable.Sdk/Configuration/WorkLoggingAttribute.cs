using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Declares default worker-scoped log capture behavior for a work executor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkLoggingAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkLoggingAttribute"/> class.
    /// </summary>
    /// <param name="isEnabled">Enables worker-scoped log capture.</param>
    /// <param name="level">The minimum log level retained by Workable for the worker.</param>
    /// <param name="maximumBufferedEntries">The maximum number of retained log entries per worker iteration.</param>
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

    /// <summary>
    /// Gets the validated logging configuration produced by the attribute.
    /// </summary>
    public WorkLoggingConfiguration Configuration { get; }
}

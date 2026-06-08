using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures worker-scoped log capture for execution and retained iteration snapshots.
/// </summary>
public sealed record WorkLoggingConfiguration
{
    /// <summary>
    /// Gets the default logging configuration.
    /// </summary>
    public static WorkLoggingConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether worker-scoped log capture is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Gets the minimum log level retained by Workable for the worker.
    /// </summary>
    public LogLevel Level { get; init; } = LogLevel.Information;

    /// <summary>
    /// Gets the maximum number of retained log entries per worker iteration.
    /// </summary>
    public int MaximumBufferedEntries { get; init; } = 100;
}

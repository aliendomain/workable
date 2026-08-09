using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures persistent logs and profiles for completed work iterations.
/// </summary>
public sealed record WorkExecutionDiagnosticsPersistenceConfiguration
{
    /// <summary>
    /// The longest supported retention period for persisted execution diagnostics.
    /// </summary>
    public static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// The shortest supported retention period for persisted execution diagnostics.
    /// </summary>
    public static readonly TimeSpan MinimumRetention = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets the inherited default. A null enabled state inherits the work-system policy.
    /// </summary>
    public static WorkExecutionDiagnosticsPersistenceConfiguration Default { get; } = new();

    /// <summary>
    /// Gets whether persistence is explicitly enabled or disabled for this work, or null to inherit the system policy.
    /// </summary>
    public bool? IsEnabled { get; init; }

    /// <summary>
    /// Gets how long each completed iteration artifact is retained.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets the minimum log level sent to persistent storage independently of retained snapshot logging.
    /// </summary>
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Gets the automatic instrumentation capture mode used when persistence enables profiling.
    /// </summary>
    public WorkProfileCaptureMode ProfileCaptureMode { get; init; } = WorkProfileCaptureMode.Bounded;
}

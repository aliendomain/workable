using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures persistent execution diagnostics inherited by work definitions in a system.
/// </summary>
public sealed record WorkSystemExecutionDiagnosticsPersistenceConfiguration
{
    /// <summary>
    /// Gets the disabled system default.
    /// </summary>
    public static WorkSystemExecutionDiagnosticsPersistenceConfiguration Default { get; } = new();

    /// <summary>
    /// Gets whether persistence is enabled for work definitions that do not override it.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets how long completed iteration artifacts are retained.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets the minimum level written to persistent storage.
    /// </summary>
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Gets the automatic profile instrumentation capture mode.
    /// </summary>
    public WorkProfileCaptureMode ProfileCaptureMode { get; init; } = WorkProfileCaptureMode.Bounded;

    /// <summary>
    /// Gets the maximum number of pending persistence operations retained in process memory.
    /// </summary>
    public int ChannelCapacity { get; init; } = 10_000;

    /// <summary>
    /// Gets the maximum number of completed live profiles retained for asynchronous materialization.
    /// </summary>
    public int MaximumPendingProfiles { get; init; } = 32;

    /// <summary>
    /// Gets the additional bounded capacity reserved for begin, completion, and flush operations.
    /// </summary>
    public int ControlOperationCapacity { get; init; } = 1_024;

    /// <summary>
    /// Gets the approximate maximum UTF-16 payload bytes retained by queued log operations.
    /// </summary>
    public long MaximumPendingLogBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum number of persisted log entries accepted for one iteration.
    /// </summary>
    public int MaximumLogsPerIteration { get; init; } = 10_000;

    /// <summary>
    /// Gets the approximate maximum UTF-16 log payload bytes accepted for one iteration artifact.
    /// </summary>
    public long MaximumLogBytesPerIteration { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum rendered message length retained for one persisted log entry.
    /// </summary>
    public int MaximumLogMessageLength { get; init; } = 16 * 1024;

    /// <summary>
    /// Gets the maximum structured property text retained for one persisted log entry.
    /// </summary>
    public int MaximumLogPropertiesLength { get; init; } = 32 * 1024;

    /// <summary>
    /// Gets the maximum exception message or stack length retained for one persisted log entry.
    /// </summary>
    public int MaximumExceptionTextLength { get; init; } = 32 * 1024;

    /// <summary>
    /// Gets the maximum number of profile nodes accepted for one persisted artifact.
    /// </summary>
    public int MaximumProfileNodeCount { get; init; } = 100_000;

    /// <summary>
    /// Gets the maximum serialized UTF-8 profile bytes accepted for one persisted artifact.
    /// </summary>
    public int MaximumProfileJsonLength { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum number of active temporary capture rules for this system.
    /// </summary>
    public int MaximumCaptureRules { get; init; } = 1_000;

    /// <summary>
    /// Gets the maximum number of log entries written in one repository batch.
    /// </summary>
    public int LogBatchSize { get; init; } = 250;

    /// <summary>
    /// Gets the number of expired artifacts deleted in one repository batch.
    /// </summary>
    public int CleanupBatchSize { get; init; } = 1_000;

    /// <summary>
    /// Gets the maximum cleanup batches drained during one cleanup interval.
    /// </summary>
    public int MaximumCleanupBatchesPerInterval { get; init; } = 10;

    /// <summary>
    /// Gets the delay between bounded cleanup passes while an expiration backlog remains.
    /// </summary>
    public TimeSpan CleanupBacklogDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets how frequently expired records are physically removed.
    /// </summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(1);
}

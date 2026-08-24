namespace Workable;

/// <summary>
/// Describes worker-level configuration changes as surfaced to authorization requirements.
/// </summary>
public sealed record WorkWorkerReconfigurationChanges(
    bool? ProfilingEnabled = null,
    WorkStartConfiguration? Start = null,
    WorkCoordinationConfiguration? Coordination = null,
    WorkRecurrenceConfiguration? Recurrence = null,
    WorkTransientRetryConfiguration? TransientRetry = null,
    WorkFailedWorkerConfiguration? FailedWorker = null,
    WorkLoggingConfiguration? Logging = null,
    WorkRetentionConfiguration? Retention = null)
{
    /// <summary>
    /// Gets the requested override for how automatic instrumentation is retained.
    /// </summary>
    public WorkProfileCaptureMode? ProfilingCaptureMode { get; init; }
}

/// <summary>
/// Describes definition-level configuration changes as surfaced to authorization requirements.
/// </summary>
public sealed record WorkDefinitionReconfigurationChanges(
    WorkerOptions? DefaultOptions = null,
    WorkConfiguration? Configuration = null);

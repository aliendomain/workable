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
    WorkRetentionConfiguration? Retention = null);

/// <summary>
/// Describes definition-level configuration changes as surfaced to authorization requirements.
/// </summary>
public sealed record WorkDefinitionReconfigurationChanges(
    WorkerOptions? DefaultOptions = null,
    WorkConfiguration? Configuration = null);

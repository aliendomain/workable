namespace Workable;

/// <summary>
/// Describes worker-level configuration changes that apply to one existing worker.
/// </summary>
/// <param name="ProfilingEnabled">An optional override for whether profiling should be enabled on the worker.</param>
/// <param name="Start">Optional worker-level start configuration changes.</param>
/// <param name="Coordination">Optional worker-level coordination changes.</param>
/// <param name="Recurrence">Optional worker-level recurrence changes.</param>
/// <param name="TransientRetry">Optional worker-level transient retry changes.</param>
/// <param name="FailedWorker">Optional failed-worker handling changes.</param>
/// <param name="Logging">Optional worker-level retained logging changes.</param>
/// <param name="Retention">Optional worker-level retention changes.</param>
public sealed record WorkerReconfiguration(
    bool? ProfilingEnabled = null,
    WorkStartConfiguration? Start = null,
    WorkCoordinationConfiguration? Coordination = null,
    WorkRecurrenceConfiguration? Recurrence = null,
    WorkTransientRetryConfiguration? TransientRetry = null,
    WorkFailedWorkerConfiguration? FailedWorker = null,
    WorkLoggingConfiguration? Logging = null,
    WorkRetentionConfiguration? Retention = null);

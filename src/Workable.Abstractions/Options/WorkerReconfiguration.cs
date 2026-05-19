namespace Workable;

public sealed record WorkerReconfiguration(
    bool? ProfilingEnabled = null,
    WorkStartConfiguration? Start = null,
    WorkCoordinationConfiguration? Coordination = null,
    WorkRecurrenceConfiguration? Recurrence = null,
    WorkTransientRetryConfiguration? TransientRetry = null,
    WorkLoggingConfiguration? Logging = null,
    WorkRetentionConfiguration? Retention = null);

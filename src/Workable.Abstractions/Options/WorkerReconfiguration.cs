namespace Workable;

public sealed record WorkerReconfiguration(
    bool? ProfilingEnabled = null,
    WorkStartConfiguration? Start = null,
    WorkIdempotencyConfiguration? Idempotency = null,
    WorkRecurrenceConfiguration? Recurrence = null,
    WorkTransientRetryConfiguration? TransientRetry = null,
    WorkLoggingConfiguration? Logging = null,
    WorkRetentionConfiguration? Retention = null,
    WorkConcurrencyConfiguration? Concurrency = null,
    WorkQueueDurabilityConfiguration? QueueDurability = null);

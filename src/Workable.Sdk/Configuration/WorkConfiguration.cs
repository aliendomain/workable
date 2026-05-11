namespace Workable;
public sealed record WorkConfiguration(
    WorkStartConfiguration Start,
    WorkIdempotencyConfiguration Idempotency,
    WorkRecurrenceConfiguration Recurrence,
    WorkTransientRetryConfiguration TransientRetry,
    WorkLoggingConfiguration Logging,
    WorkRetentionConfiguration Retention,
    WorkConcurrencyConfiguration Concurrency,
    WorkInvocationConfiguration Invocation)
{
    public static WorkConfiguration Default { get; } = new(
        WorkStartConfiguration.Default,
        WorkIdempotencyConfiguration.Default,
        WorkRecurrenceConfiguration.Default,
        WorkTransientRetryConfiguration.Default,
        WorkLoggingConfiguration.Default,
        WorkRetentionConfiguration.Default,
        WorkConcurrencyConfiguration.Default,
        WorkInvocationConfiguration.Default);

    public WorkConfiguration Merge(WorkConfiguration? overrides)
        => overrides is null
            ? this
            : this with
            {
                Start = overrides.Start,
                Idempotency = overrides.Idempotency,
                Recurrence = overrides.Recurrence,
                TransientRetry = overrides.TransientRetry,
                Logging = overrides.Logging,
                Retention = overrides.Retention,
                Concurrency = overrides.Concurrency,
            };
}

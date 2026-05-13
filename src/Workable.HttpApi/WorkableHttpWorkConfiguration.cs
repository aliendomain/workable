namespace Workable;

public sealed record WorkableHttpWorkConfiguration(
    WorkStartConfiguration Start,
    WorkIdempotencyConfiguration Idempotency,
    WorkRecurrenceConfiguration Recurrence,
    WorkTransientRetryConfiguration TransientRetry,
    WorkLoggingConfiguration Logging,
    WorkRetentionConfiguration Retention,
    WorkConcurrencyConfiguration Concurrency)
{
    public static WorkableHttpWorkConfiguration Default { get; } = From(WorkConfiguration.Default);

    public static WorkableHttpWorkConfiguration From(WorkConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new WorkableHttpWorkConfiguration(
            configuration.Start,
            configuration.Idempotency,
            configuration.Recurrence,
            configuration.TransientRetry,
            configuration.Logging,
            configuration.Retention,
            configuration.Concurrency);
    }

    internal WorkConfiguration ToWorkConfiguration()
        => new(
            Start,
            Idempotency,
            Recurrence,
            TransientRetry,
            Logging,
            Retention,
            Concurrency,
            WorkInvocationConfiguration.Default);
}

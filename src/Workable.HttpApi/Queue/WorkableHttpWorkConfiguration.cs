namespace Workable;

public sealed record WorkableHttpWorkConfiguration(
    WorkStartConfiguration Start,
    WorkCoordinationConfiguration Coordination,
    WorkRecurrenceConfiguration Recurrence,
    WorkTransientRetryConfiguration TransientRetry,
    WorkLoggingConfiguration Logging,
    WorkRetentionConfiguration Retention)
{
    public static WorkableHttpWorkConfiguration Default { get; } = From(WorkConfiguration.Default);

    public static WorkableHttpWorkConfiguration From(WorkConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new WorkableHttpWorkConfiguration(
            configuration.Start,
            configuration.Coordination,
            configuration.Recurrence,
            configuration.TransientRetry,
            configuration.Logging,
            configuration.Retention);
    }

    internal WorkConfiguration ToWorkConfiguration()
        => new(
            Start,
            Coordination,
            Recurrence,
            TransientRetry,
            Logging,
            Retention,
            WorkInvocationConfiguration.Default);
}

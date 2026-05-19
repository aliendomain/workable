namespace Workable;
public sealed record WorkConfiguration(
    WorkStartConfiguration Start,
    WorkCoordinationConfiguration Coordination,
    WorkRecurrenceConfiguration Recurrence,
    WorkTransientRetryConfiguration TransientRetry,
    WorkLoggingConfiguration Logging,
    WorkRetentionConfiguration Retention,
    WorkInvocationConfiguration Invocation)
{
    public static WorkConfiguration Default { get; } = new(
        WorkStartConfiguration.Default,
        WorkCoordinationConfiguration.Default,
        WorkRecurrenceConfiguration.Default,
        WorkTransientRetryConfiguration.Default,
        WorkLoggingConfiguration.Default,
        WorkRetentionConfiguration.Default,
        WorkInvocationConfiguration.Default);

    public WorkConfiguration MergeRuntimeOptions(WorkConfiguration? overrides)
        => overrides is null
            ? this
            : this with
            {
                Start = overrides.Start,
                Coordination = overrides.Coordination,
                Recurrence = overrides.Recurrence,
                TransientRetry = overrides.TransientRetry,
                Logging = overrides.Logging,
                Retention = overrides.Retention,
                // Invocation is intentionally excluded. Allowed invocation channels are a
                // design-time contract for the work definition, not a runtime worker option.
            };

    public WorkConfiguration Merge(WorkConfiguration? overrides)
        => this.MergeRuntimeOptions(overrides);
}

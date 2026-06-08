namespace Workable;

/// <summary>
/// Represents the HTTP-serializable per-request runtime configuration override for queued work.
/// </summary>
/// <param name="Start">The per-request start behavior override.</param>
/// <param name="Coordination">The per-request coordination and durability override.</param>
/// <param name="Recurrence">The per-request recurrence override.</param>
/// <param name="TransientRetry">The per-request transient retry override.</param>
/// <param name="Logging">The per-request log-capture override.</param>
/// <param name="Retention">The per-request retention override.</param>
public sealed record WorkableHttpWorkConfiguration(
    WorkStartConfiguration Start,
    WorkCoordinationConfiguration Coordination,
    WorkRecurrenceConfiguration Recurrence,
    WorkTransientRetryConfiguration TransientRetry,
    WorkLoggingConfiguration Logging,
    WorkRetentionConfiguration Retention)
{
    /// <summary>
    /// Gets the default HTTP work-configuration projection based on <see cref="WorkConfiguration.Default"/>.
    /// </summary>
    public static WorkableHttpWorkConfiguration Default { get; } = From(WorkConfiguration.Default);

    /// <summary>
    /// Projects a core <see cref="WorkConfiguration"/> into the HTTP-serializable shape.
    /// </summary>
    /// <param name="configuration">The core configuration to project.</param>
    /// <returns>The projected HTTP work configuration.</returns>
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

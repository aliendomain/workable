namespace Workable;

/// <summary>
/// Declares default transient retry behavior for a work executor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkTransientRetryAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkTransientRetryAttribute"/> class.
    /// </summary>
    /// <param name="count">The number of transient retry attempts after the initial execution fails.</param>
    /// <param name="initialDelayMilliseconds">The initial retry delay, in milliseconds.</param>
    /// <param name="jitterMilliseconds">The maximum random delay, in milliseconds, added to each retry delay.</param>
    /// <param name="maximumDelayMilliseconds">The maximum retry delay, in milliseconds.</param>
    /// <param name="backoff">The retry delay strategy.</param>
    public WorkTransientRetryAttribute(
        int count = 0,
        int initialDelayMilliseconds = 800,
        int jitterMilliseconds = 500,
        int maximumDelayMilliseconds = 30_000,
        WorkRetryBackoff backoff = WorkRetryBackoff.Exponential)
    {
        this.Configuration = new WorkTransientRetryConfiguration
        {
            Count = count,
            InitialDelay = TimeSpan.FromMilliseconds(initialDelayMilliseconds),
            Jitter = TimeSpan.FromMilliseconds(jitterMilliseconds),
            MaximumDelay = TimeSpan.FromMilliseconds(maximumDelayMilliseconds),
            Backoff = backoff,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { TransientRetry = this.Configuration });
    }

    /// <summary>
    /// Gets the validated transient retry configuration produced by the attribute.
    /// </summary>
    public WorkTransientRetryConfiguration Configuration { get; }
}

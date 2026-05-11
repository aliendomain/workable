namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkTransientRetryAttribute : Attribute
{
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

    public WorkTransientRetryConfiguration Configuration { get; }
}

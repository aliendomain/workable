namespace Workable;

/// <summary>
/// Configures retry behavior for transient execution failures.
/// </summary>
public sealed record WorkTransientRetryConfiguration
{
    /// <summary>
    /// Gets the default transient retry configuration.
    /// </summary>
    public static WorkTransientRetryConfiguration Default { get; } = new()
    {
        Count = 3,
    };

    /// <summary>
    /// Gets a transient retry configuration that disables retries.
    /// </summary>
    public static WorkTransientRetryConfiguration Disabled { get; } = new();

    /// <summary>
    /// Gets the number of retry attempts after the initial transient failure.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Gets the delay before the first retry attempt.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Gets the maximum random delay added to each retry attempt.
    /// </summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets the upper bound for retry delay after applying backoff.
    /// </summary>
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the strategy used to calculate retry delay across attempts.
    /// </summary>
    public WorkRetryBackoff Backoff { get; init; } = WorkRetryBackoff.Exponential;
}

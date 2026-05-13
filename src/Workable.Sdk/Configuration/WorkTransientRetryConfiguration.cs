namespace Workable;
public sealed record WorkTransientRetryConfiguration
{
    public static WorkTransientRetryConfiguration Default { get; } = new()
    {
        Count = 3,
    };

    public static WorkTransientRetryConfiguration Disabled { get; } = new();

    public int Count { get; init; }

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan Jitter { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    public WorkRetryBackoff Backoff { get; init; } = WorkRetryBackoff.Exponential;
}

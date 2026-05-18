namespace Workable;

public sealed record WorkQueueDurabilityConfiguration
{
    public static TimeSpan DefaultFallbackPollingInterval { get; } = TimeSpan.FromSeconds(5);

    public static TimeSpan MinimumFallbackPollingInterval { get; } = TimeSpan.FromSeconds(1);

    public static WorkQueueDurabilityConfiguration Default { get; } = new();

    public bool IsEnabled { get; init; }

    public bool CompleteDurably { get; init; }

    public TimeSpan FallbackPollingInterval { get; init; } = DefaultFallbackPollingInterval;
}

namespace Workable;

/// <summary>
/// Configures durable queue persistence and optional durable completion requirements.
/// </summary>
public sealed record WorkQueueDurabilityConfiguration
{
    /// <summary>
    /// Gets the default fallback polling interval used for durable queue discovery.
    /// </summary>
    public static TimeSpan DefaultFallbackPollingInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the minimum supported fallback polling interval when durable queueing is enabled.
    /// </summary>
    public static TimeSpan MinimumFallbackPollingInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the default durable queue configuration with durable queueing disabled.
    /// </summary>
    public static WorkQueueDurabilityConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether accepted work is first persisted to the durable queue store.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether successful execution must durably complete inside a caller-owned transaction.
    /// </summary>
    public bool CompleteDurably { get; init; }

    /// <summary>
    /// Gets the polling interval used when durable work cannot be discovered by an immediate local signal.
    /// </summary>
    public TimeSpan FallbackPollingInterval { get; init; } = DefaultFallbackPollingInterval;
}

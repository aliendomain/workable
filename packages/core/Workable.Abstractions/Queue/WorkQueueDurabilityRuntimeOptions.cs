namespace Workable;

/// <summary>
/// Runtime tuning options for durable queue readers.
/// </summary>
public sealed record WorkQueueDurabilityRuntimeOptions
{
    public const int DefaultClaimBatchSize = 7_500;

    public static WorkQueueDurabilityRuntimeOptions Default { get; } = new();

    public int ClaimBatchSize { get; init; } = DefaultClaimBatchSize;

    public int RecentClaimSampleCapacity { get; init; }
}

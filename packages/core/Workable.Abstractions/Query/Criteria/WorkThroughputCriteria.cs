namespace Workable;

/// <summary>
/// Controls the time window and bucket sizing for throughput queries.
/// </summary>
/// <param name="WindowSeconds">The total lookback window, in seconds.</param>
/// <param name="BucketSeconds">The bucket size, in seconds, used to aggregate throughput counts.</param>
public sealed record WorkThroughputCriteria(
    int WindowSeconds = WorkThroughputCriteria.DefaultWindowSeconds,
    int BucketSeconds = WorkThroughputCriteria.DefaultBucketSeconds)
{
    /// <summary>
    /// The default throughput lookback window, in seconds.
    /// </summary>
    public const int DefaultWindowSeconds = 60;
    /// <summary>
    /// The default throughput bucket size, in seconds.
    /// </summary>
    public const int DefaultBucketSeconds = 1;
    /// <summary>
    /// The maximum throughput lookback window, in seconds.
    /// </summary>
    public const int MaximumWindowSeconds = 3_600;
}

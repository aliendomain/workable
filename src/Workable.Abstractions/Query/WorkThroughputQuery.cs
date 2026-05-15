namespace Workable;

public sealed record WorkThroughputQuery(
    int WindowSeconds = WorkThroughputQuery.DefaultWindowSeconds,
    int BucketSeconds = WorkThroughputQuery.DefaultBucketSeconds)
{
    public const int DefaultWindowSeconds = 60;
    public const int DefaultBucketSeconds = 1;
    public const int MaximumWindowSeconds = 3_600;
}

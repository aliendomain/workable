namespace Workable;

public sealed record WorkThroughputCriteria(
    int WindowSeconds = WorkThroughputCriteria.DefaultWindowSeconds,
    int BucketSeconds = WorkThroughputCriteria.DefaultBucketSeconds)
{
    public const int DefaultWindowSeconds = 60;
    public const int DefaultBucketSeconds = 1;
    public const int MaximumWindowSeconds = 3_600;
}

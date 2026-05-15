namespace Workable;

public sealed record WorkSystemThroughput(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowSeconds,
    int BucketSeconds,
    IReadOnlyList<WorkThroughputBucket> Buckets,
    WorkThroughputLiveSummary LiveSummary);

public sealed record WorkThroughputBucket(
    DateTimeOffset At,
    int Queued,
    int Succeeded,
    int Failed,
    double AverageExecutionMilliseconds);

public sealed record WorkThroughputLiveSummary(
    int WindowSeconds,
    double QueuedPerSecond,
    double SucceededPerSecond,
    double FailedPerSecond,
    double QueueDeltaPerSecond,
    double AverageExecutionMilliseconds);

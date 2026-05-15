namespace Workable;

public sealed record WorkSystemThroughput(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowSeconds,
    int BucketSeconds,
    IReadOnlyList<WorkThroughputBucket> Buckets,
    WorkThroughputLiveSummary LiveSummary) : IWorkQueryResult;

public sealed record WorkThroughputBucket(
    DateTimeOffset At,
    int Started,
    int Completed,
    int Failed,
    int Canceled,
    double AverageExecutionMilliseconds);

public sealed record WorkThroughputLiveSummary(
    int WindowSeconds,
    double StartedPerSecond,
    double CompletedPerSecond,
    double FailedPerSecond,
    double CanceledPerSecond,
    double InFlightDeltaPerSecond,
    double AverageExecutionMilliseconds);

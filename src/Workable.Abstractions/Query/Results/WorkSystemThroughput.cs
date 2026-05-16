namespace Workable;

public sealed record WorkSystemThroughput(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowSeconds,
    int BucketSeconds,
    IReadOnlyList<WorkThroughputBucket> Buckets,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkThroughputLiveSummary LiveSummary) : IWorkQueryResult;

public sealed record WorkSystemThroughputSummary(
    int WindowSeconds,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkThroughputLiveSummary LiveSummary) : IWorkQueryResult;

public sealed record WorkThroughputExecutionSummary(
    int ExecutionCount,
    double AverageExecutionMilliseconds,
    double SlowestExecutionMilliseconds,
    double P95ExecutionMilliseconds,
    double P99ExecutionMilliseconds);

public sealed record WorkThroughputBucket(
    DateTimeOffset At,
    int Started,
    int Completed,
    int Failed,
    int Canceled,
    double AverageExecutionMilliseconds,
    int ExecutionCount,
    double SlowestExecutionMilliseconds,
    double P95ExecutionMilliseconds,
    double P99ExecutionMilliseconds);

public sealed record WorkThroughputLiveSummary(
    int WindowSeconds,
    double StartedPerSecond,
    double CompletedPerSecond,
    double FailedPerSecond,
    double CanceledPerSecond,
    double InFlightDeltaPerSecond,
    double AverageExecutionMilliseconds,
    int ExecutionCount,
    double SlowestExecutionMilliseconds,
    double P95ExecutionMilliseconds,
    double P99ExecutionMilliseconds);

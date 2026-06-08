namespace Workable;

/// <summary>
/// Represents bucketed throughput metrics for a scoped system window.
/// </summary>
/// <param name="From">The inclusive start of the sampled window.</param>
/// <param name="To">The inclusive end of the sampled window.</param>
/// <param name="WindowSeconds">The total size of the sampled window in seconds.</param>
/// <param name="BucketSeconds">The width of each returned bucket in seconds.</param>
/// <param name="SettledCount">The number of settled iterations represented by the sample.</param>
/// <param name="Buckets">The time buckets that make up the sampled window.</param>
/// <param name="ExecutionSummary">Aggregated execution latency metrics for the sampled window.</param>
/// <param name="LiveSummary">Rate-oriented live metrics derived from the sampled window.</param>
public sealed record WorkSystemThroughput(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowSeconds,
    int BucketSeconds,
    int SettledCount,
    IReadOnlyList<WorkThroughputBucket> Buckets,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkThroughputLiveSummary LiveSummary) : IWorkQueryResult;

/// <summary>
/// Represents a compact throughput summary without per-bucket detail.
/// </summary>
/// <param name="WindowSeconds">The total size of the summarized window in seconds.</param>
/// <param name="SettledCount">The number of settled iterations represented by the summary.</param>
/// <param name="ExecutionSummary">Aggregated execution latency metrics for the window.</param>
/// <param name="LiveSummary">Rate-oriented live metrics derived from the window.</param>
public sealed record WorkSystemThroughputSummary(
    int WindowSeconds,
    int SettledCount,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkThroughputLiveSummary LiveSummary) : IWorkQueryResult;

/// <summary>
/// Represents aggregate execution latency statistics for a throughput sample.
/// </summary>
/// <param name="ExecutionCount">The number of executions included in the latency calculations.</param>
/// <param name="AverageExecutionMilliseconds">The mean execution duration in milliseconds.</param>
/// <param name="SlowestExecutionMilliseconds">The slowest execution duration in milliseconds.</param>
/// <param name="P95ExecutionMilliseconds">The 95th percentile execution duration in milliseconds.</param>
/// <param name="P99ExecutionMilliseconds">The 99th percentile execution duration in milliseconds.</param>
public sealed record WorkThroughputExecutionSummary(
    int ExecutionCount,
    double AverageExecutionMilliseconds,
    double SlowestExecutionMilliseconds,
    double P95ExecutionMilliseconds,
    double P99ExecutionMilliseconds);

/// <summary>
/// Represents one time bucket inside a throughput sample.
/// </summary>
/// <param name="At">The bucket timestamp.</param>
/// <param name="Started">The number of iterations that started in the bucket.</param>
/// <param name="Completed">The number of iterations that completed successfully in the bucket.</param>
/// <param name="Failed">The number of iterations that failed in the bucket.</param>
/// <param name="Canceled">The number of iterations that were canceled in the bucket.</param>
/// <param name="AverageExecutionMilliseconds">The mean execution duration for executions in the bucket.</param>
/// <param name="ExecutionCount">The number of executions included in the bucket latency metrics.</param>
/// <param name="SlowestExecutionMilliseconds">The slowest execution duration in the bucket.</param>
/// <param name="P95ExecutionMilliseconds">The 95th percentile execution duration in the bucket.</param>
/// <param name="P99ExecutionMilliseconds">The 99th percentile execution duration in the bucket.</param>
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

/// <summary>
/// Represents rate-oriented live throughput metrics for a sampled window.
/// </summary>
/// <param name="WindowSeconds">The total size of the sampled window in seconds.</param>
/// <param name="StartedPerSecond">The average iteration start rate during the window.</param>
/// <param name="CompletedPerSecond">The average successful completion rate during the window.</param>
/// <param name="FailedPerSecond">The average failure rate during the window.</param>
/// <param name="CanceledPerSecond">The average cancellation rate during the window.</param>
/// <param name="InFlightDeltaPerSecond">The average rate of change in in-flight work during the window.</param>
/// <param name="AverageExecutionMilliseconds">The mean execution duration in milliseconds.</param>
/// <param name="ExecutionCount">The number of executions included in the latency calculations.</param>
/// <param name="SlowestExecutionMilliseconds">The slowest execution duration in milliseconds.</param>
/// <param name="P95ExecutionMilliseconds">The 95th percentile execution duration in milliseconds.</param>
/// <param name="P99ExecutionMilliseconds">The 99th percentile execution duration in milliseconds.</param>
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

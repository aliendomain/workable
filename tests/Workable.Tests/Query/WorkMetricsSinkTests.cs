using Workable;

namespace Workable.Tests;

[Trait("Category", "Query")]
public sealed class WorkMetricsSinkTests
{
    [Fact]
    public void ThroughputUsesMinuteRollupsAfterSecondBucketsArePruned()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var oldDefinitionId = new WorkDefinitionId(Guid.NewGuid());
        var currentDefinitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var old = now.AddMinutes(-20);

        metrics.IterationRecorded(oldDefinitionId, StartedIteration(old.AddMilliseconds(-50)));
        metrics.IterationRecorded(oldDefinitionId, CompletedIteration(old));

        metrics.IterationRecorded(currentDefinitionId, StartedIteration(now));

        var minuteThroughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 3_600, BucketSeconds: 60),
            new HashSet<WorkDefinitionId> { oldDefinitionId });
        var secondThroughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 3_600, BucketSeconds: 15),
            new HashSet<WorkDefinitionId> { oldDefinitionId });
        var currentThroughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { currentDefinitionId });

        Assert.Equal(1, minuteThroughput.Buckets.Sum(bucket => bucket.Started));
        Assert.Equal(1, minuteThroughput.Buckets.Sum(bucket => bucket.Completed));
        Assert.Equal(1, minuteThroughput.SettledCount);
        Assert.Equal(1, minuteThroughput.Buckets.Sum(bucket => bucket.ExecutionCount));
        Assert.Equal(50, minuteThroughput.Buckets.Max(bucket => bucket.SlowestExecutionMilliseconds));
        Assert.Empty(secondThroughput.Buckets);
        Assert.Equal(1 / 60.0, currentThroughput.LiveSummary.StartedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, currentThroughput.LiveSummary.InFlightDeltaPerSecond, precision: 6);
    }

    [Fact]
    public void ThroughputSummarizesExecutionDistribution()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();
        TimeSpan[] durations =
        [
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1)
        ];

        foreach (var duration in durations)
        {
            metrics.IterationRecorded(definitionId, CompletedIteration(now, duration));
        }

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(durations.Length, bucket.ExecutionCount);
        Assert.Equal(durations.Average(duration => duration.TotalMilliseconds), bucket.AverageExecutionMilliseconds);
        Assert.Equal(TimeSpan.FromMinutes(1).TotalMilliseconds, bucket.SlowestExecutionMilliseconds);
        Assert.Equal(45_000, bucket.P95ExecutionMilliseconds);
        Assert.Equal(57_000, bucket.P99ExecutionMilliseconds, precision: 6);
        Assert.Equal(durations.Length, throughput.SettledCount);
        Assert.Equal(durations.Length, throughput.ExecutionSummary.ExecutionCount);
        Assert.Equal(bucket.AverageExecutionMilliseconds, throughput.ExecutionSummary.AverageExecutionMilliseconds);
        Assert.Equal(bucket.SlowestExecutionMilliseconds, throughput.ExecutionSummary.SlowestExecutionMilliseconds);
        Assert.Equal(bucket.P95ExecutionMilliseconds, throughput.ExecutionSummary.P95ExecutionMilliseconds);
        Assert.Equal(bucket.P99ExecutionMilliseconds, throughput.ExecutionSummary.P99ExecutionMilliseconds);
        Assert.Equal(durations.Length, throughput.LiveSummary.ExecutionCount);
        Assert.Equal(bucket.SlowestExecutionMilliseconds, throughput.LiveSummary.SlowestExecutionMilliseconds);
        Assert.Equal(bucket.P95ExecutionMilliseconds, throughput.LiveSummary.P95ExecutionMilliseconds);
        Assert.Equal(bucket.P99ExecutionMilliseconds, throughput.LiveSummary.P99ExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputCountsFailedAndCanceledIterationsButExcludesThemFromExecutionDistribution()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();

        metrics.IterationRecorded(definitionId, StartedIteration(now));
        metrics.IterationRecorded(definitionId, Iteration(now, TimeSpan.FromMilliseconds(100), WorkCompletionStatus.Completed));
        metrics.IterationRecorded(definitionId, Iteration(now, TimeSpan.FromMilliseconds(250), WorkCompletionStatus.Failed));
        metrics.IterationRecorded(definitionId, Iteration(now, TimeSpan.FromMilliseconds(700), WorkCompletionStatus.Canceled));

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(1, bucket.Started);
        Assert.Equal(1, bucket.Completed);
        Assert.Equal(1, bucket.Failed);
        Assert.Equal(1, bucket.Canceled);
        Assert.Equal(3, throughput.SettledCount);
        Assert.Equal(1, bucket.ExecutionCount);
        Assert.Equal(100, bucket.AverageExecutionMilliseconds);
        Assert.Equal(100, bucket.SlowestExecutionMilliseconds);
        Assert.Equal(98.75, bucket.P95ExecutionMilliseconds);
        Assert.Equal(99.75, bucket.P99ExecutionMilliseconds);
        Assert.Equal(1, throughput.ExecutionSummary.ExecutionCount);
        Assert.Equal(100, throughput.ExecutionSummary.AverageExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputDistributionIsScopedByDefinition()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var includedDefinitionId = new WorkDefinitionId(Guid.NewGuid());
        var excludedDefinitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();

        metrics.IterationRecorded(includedDefinitionId, CompletedIteration(now, TimeSpan.FromMilliseconds(100)));
        metrics.IterationRecorded(includedDefinitionId, CompletedIteration(now, TimeSpan.FromMilliseconds(900)));
        metrics.IterationRecorded(excludedDefinitionId, CompletedIteration(now, TimeSpan.FromSeconds(10)));

        var scopedThroughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { includedDefinitionId });
        var systemThroughput = metrics.GetThroughput(new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1));
        var scopedBucket = Assert.Single(scopedThroughput.Buckets);
        var systemBucket = Assert.Single(systemThroughput.Buckets);

        Assert.Equal(2, scopedBucket.ExecutionCount);
        Assert.Equal(500, scopedBucket.AverageExecutionMilliseconds);
        Assert.Equal(900, scopedBucket.SlowestExecutionMilliseconds);
        Assert.Equal(900, scopedBucket.P95ExecutionMilliseconds);
        Assert.Equal(2, scopedThroughput.ExecutionSummary.ExecutionCount);
        Assert.Equal(500, scopedThroughput.ExecutionSummary.AverageExecutionMilliseconds);
        Assert.Equal(900, scopedThroughput.ExecutionSummary.SlowestExecutionMilliseconds);
        Assert.Equal(900, scopedThroughput.ExecutionSummary.P95ExecutionMilliseconds);
        Assert.Equal(2, scopedThroughput.SettledCount);
        Assert.Equal(3, systemThroughput.SettledCount);
        Assert.Equal(3, systemBucket.ExecutionCount);
        Assert.Equal(10_000, systemBucket.SlowestExecutionMilliseconds);
        Assert.Equal(9_625, systemBucket.P95ExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputDistributionReportsZeroWhenOnlyIterationsStarted()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();

        metrics.IterationRecorded(definitionId, StartedIteration(now));

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(1, bucket.Started);
        Assert.Equal(0, bucket.ExecutionCount);
        Assert.Equal(0, bucket.AverageExecutionMilliseconds);
        Assert.Equal(0, bucket.SlowestExecutionMilliseconds);
        Assert.Equal(0, bucket.P95ExecutionMilliseconds);
        Assert.Equal(0, bucket.P99ExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputDistributionPreservesZeroDurationExecutions()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();

        metrics.IterationRecorded(definitionId, CompletedIteration(now, TimeSpan.Zero));

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(1, bucket.ExecutionCount);
        Assert.Equal(0, bucket.AverageExecutionMilliseconds);
        Assert.Equal(0, bucket.SlowestExecutionMilliseconds);
        Assert.Equal(0, bucket.P95ExecutionMilliseconds);
        Assert.Equal(0, bucket.P99ExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputDistributionCapsPercentilesAtSlowestExecution()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();

        for (var index = 0; index < 100; index++)
        {
            metrics.IterationRecorded(definitionId, CompletedIteration(now, TimeSpan.FromMilliseconds(10_500)));
        }

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(10_500, bucket.SlowestExecutionMilliseconds);
        Assert.Equal(bucket.SlowestExecutionMilliseconds, bucket.P95ExecutionMilliseconds);
        Assert.Equal(bucket.SlowestExecutionMilliseconds, bucket.P99ExecutionMilliseconds);
        Assert.Equal(throughput.ExecutionSummary.SlowestExecutionMilliseconds, throughput.ExecutionSummary.P95ExecutionMilliseconds);
        Assert.Equal(throughput.ExecutionSummary.SlowestExecutionMilliseconds, throughput.ExecutionSummary.P99ExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputDistributionUsesFastWorkOptimizedHistogramBuckets()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();

        for (var index = 0; index < 95; index++)
        {
            metrics.IterationRecorded(definitionId, CompletedIteration(now, TimeSpan.FromMilliseconds(125)));
        }

        for (var index = 0; index < 5; index++)
        {
            metrics.IterationRecorded(definitionId, CompletedIteration(now, TimeSpan.FromMinutes(5)));
        }

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(150, bucket.P95ExecutionMilliseconds);
        Assert.Equal(bucket.P95ExecutionMilliseconds, throughput.ExecutionSummary.P95ExecutionMilliseconds);
    }

    [Fact]
    public void ThroughputDistributionHandlesConcurrentRecording()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());
        var now = ClosedSecond();
        const int iterationCount = 1_000;

        Parallel.For(0, iterationCount, index =>
        {
            var duration = TimeSpan.FromMilliseconds((index % 10) + 1);
            metrics.IterationRecorded(definitionId, CompletedIteration(now, duration));
        });

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1),
            new HashSet<WorkDefinitionId> { definitionId });
        var bucket = Assert.Single(throughput.Buckets);

        Assert.Equal(iterationCount, bucket.Completed);
        Assert.Equal(iterationCount, bucket.ExecutionCount);
        Assert.Equal(5.5, bucket.AverageExecutionMilliseconds);
        Assert.Equal(10, bucket.SlowestExecutionMilliseconds);
        Assert.Equal(9.5, bucket.P95ExecutionMilliseconds);
        Assert.Equal(9.9, bucket.P99ExecutionMilliseconds, precision: 6);
        Assert.Equal(iterationCount, throughput.ExecutionSummary.ExecutionCount);
        Assert.Equal(5.5, throughput.ExecutionSummary.AverageExecutionMilliseconds);
        Assert.Equal(10, throughput.ExecutionSummary.SlowestExecutionMilliseconds);
        Assert.Equal(9.5, throughput.ExecutionSummary.P95ExecutionMilliseconds);
        Assert.Equal(9.9, throughput.ExecutionSummary.P99ExecutionMilliseconds, precision: 6);
    }

    [Fact]
    public void ThroughputBucketsExcludeCurrentOpenBucket()
    {
        var metrics = new InMemoryWorkMetricsSink();
        var definitionId = new WorkDefinitionId(Guid.NewGuid());

        metrics.IterationRecorded(definitionId, CompletedIteration(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(100)));

        var throughput = metrics.GetThroughput(
            new WorkThroughputCriteria(WindowSeconds: 3_600, BucketSeconds: 3_600),
            new HashSet<WorkDefinitionId> { definitionId });

        Assert.Empty(throughput.Buckets);
        Assert.Equal(0, throughput.ExecutionSummary.ExecutionCount);
        Assert.Equal(1, throughput.LiveSummary.ExecutionCount);
        Assert.Equal(100, throughput.LiveSummary.AverageExecutionMilliseconds);
    }

    private static DateTimeOffset ClosedSecond()
        => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);

    private static WorkerIterationSnapshot StartedIteration(DateTimeOffset startedAt)
        => new(
            Sequence: 1,
            StartedAt: startedAt,
            CompletedAt: startedAt,
            ExecutionDuration: TimeSpan.Zero,
            Status: WorkCompletionStatus.Executing,
            Output: null,
            Messages: []);

    private static WorkerIterationSnapshot CompletedIteration(DateTimeOffset completedAt)
        => CompletedIteration(completedAt, TimeSpan.FromMilliseconds(50));

    private static WorkerIterationSnapshot CompletedIteration(
        DateTimeOffset completedAt,
        TimeSpan executionDuration)
        => Iteration(completedAt, executionDuration, WorkCompletionStatus.Completed);

    private static WorkerIterationSnapshot Iteration(
        DateTimeOffset completedAt,
        TimeSpan executionDuration,
        WorkCompletionStatus status)
        => new(
            Sequence: 1,
            StartedAt: completedAt - executionDuration,
            CompletedAt: completedAt,
            ExecutionDuration: executionDuration,
            Status: status,
            Output: null,
            Messages: []);
}

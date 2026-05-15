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
        Assert.Empty(secondThroughput.Buckets);
        Assert.Equal(1 / 60.0, currentThroughput.LiveSummary.StartedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, currentThroughput.LiveSummary.InFlightDeltaPerSecond, precision: 6);
    }

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
        => new(
            Sequence: 1,
            StartedAt: completedAt.AddMilliseconds(-50),
            CompletedAt: completedAt,
            ExecutionDuration: TimeSpan.FromMilliseconds(50),
            Status: WorkCompletionStatus.Completed,
            Output: null,
            Messages: []);
}

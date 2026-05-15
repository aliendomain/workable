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

        metrics.WorkerQueued(oldDefinitionId, old);
        metrics.IterationCompleted(oldDefinitionId, CompletedIteration(old));

        metrics.WorkerQueued(currentDefinitionId, now);

        var minuteThroughput = metrics.GetThroughput(
            new WorkThroughputQuery(WindowSeconds: 3_600, BucketSeconds: 60),
            new HashSet<WorkDefinitionId> { oldDefinitionId });
        var secondThroughput = metrics.GetThroughput(
            new WorkThroughputQuery(WindowSeconds: 3_600, BucketSeconds: 15),
            new HashSet<WorkDefinitionId> { oldDefinitionId });

        Assert.Equal(1, minuteThroughput.Buckets.Sum(bucket => bucket.Queued));
        Assert.Equal(1, minuteThroughput.Buckets.Sum(bucket => bucket.Succeeded));
        Assert.Empty(secondThroughput.Buckets);
    }

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

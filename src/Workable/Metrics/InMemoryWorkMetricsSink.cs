using System.Collections.Concurrent;

namespace Workable;

internal sealed class InMemoryWorkMetricsSink : IWorkMetricsSink
{
    private const int LiveSummaryWindowSeconds = 60;
    private const int SecondBucketRetentionSeconds = 15 * 60;
    private const int BucketRetentionBufferSeconds = 60;
    private const int MinuteResolutionSeconds = 60;

    private readonly MetricStore secondBuckets = new();
    private readonly MetricStore minuteBuckets = new();
    private long lastPrunedSecond;

    public void IterationRecorded(WorkDefinitionId definitionId, WorkerIterationSnapshot iteration)
    {
        if (iteration.Status is not (
            WorkCompletionStatus.Executing or
            WorkCompletionStatus.Completed or
            WorkCompletionStatus.Failed or
            WorkCompletionStatus.Canceled))
        {
            return;
        }

        var second = (iteration.Status == WorkCompletionStatus.Executing
            ? iteration.StartedAt
            : iteration.CompletedAt).ToUnixTimeSeconds();
        this.Record(
            definitionId,
            second,
            bucket =>
            {
                switch (iteration.Status)
                {
                    case WorkCompletionStatus.Executing:
                        bucket.IncrementStarted();
                        break;
                    case WorkCompletionStatus.Completed:
                        bucket.IncrementCompleted();
                        bucket.AddExecution(iteration.ExecutionDuration);
                        break;
                    case WorkCompletionStatus.Failed:
                        bucket.IncrementFailed();
                        bucket.AddExecution(iteration.ExecutionDuration);
                        break;
                    case WorkCompletionStatus.Canceled:
                        bucket.IncrementCanceled();
                        bucket.AddExecution(iteration.ExecutionDuration);
                        break;
                }
            });
        this.PruneIfDue(second);
    }

    public WorkSystemThroughput GetThroughput(
        WorkThroughputQuery? query = null,
        IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var normalized = Normalize(query);
        var toSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fromSecond = toSecond - normalized.WindowSeconds + 1;
        var requestedFirstBucketSecond = FloorToBucket(fromSecond, normalized.BucketSeconds);
        var source = this.GetQuerySource(normalized);
        var firstBucketSecond = this.FindFirstBucketWithData(
            requestedFirstBucketSecond,
            toSecond,
            normalized.BucketSeconds,
            source,
            definitionIds);
        if (firstBucketSecond is null)
        {
            return new WorkSystemThroughput(
                DateTimeOffset.FromUnixTimeSeconds(toSecond),
                DateTimeOffset.FromUnixTimeSeconds(toSecond),
                normalized.WindowSeconds,
                normalized.BucketSeconds,
                [],
                this.CreateLiveSummary(toSecond, definitionIds));
        }

        var buckets = new List<WorkThroughputBucket>();

        for (var bucketSecond = firstBucketSecond.Value; bucketSecond <= toSecond; bucketSecond += normalized.BucketSeconds)
        {
            var bucketEnd = Math.Min(toSecond, bucketSecond + normalized.BucketSeconds - 1);
            var aggregate = this.Aggregate(source, bucketSecond, bucketEnd, definitionIds);
            buckets.Add(ToThroughputBucket(bucketSecond, aggregate));
        }

        return new WorkSystemThroughput(
            DateTimeOffset.FromUnixTimeSeconds(firstBucketSecond.Value),
            DateTimeOffset.FromUnixTimeSeconds(toSecond),
            normalized.WindowSeconds,
            normalized.BucketSeconds,
            buckets,
            this.CreateLiveSummary(toSecond, definitionIds));
    }

    private void Record(
        WorkDefinitionId definitionId,
        long second,
        Action<WorkMetricBucket> update)
    {
        update(this.secondBuckets.GetDefinitionBucket(definitionId, second));
        update(this.secondBuckets.GetSystemBucket(second));

        var minute = FloorToBucket(second, MinuteResolutionSeconds);
        update(this.minuteBuckets.GetDefinitionBucket(definitionId, minute));
        update(this.minuteBuckets.GetSystemBucket(minute));
    }

    private MetricQuerySource GetQuerySource(WorkThroughputQuery query)
        => query.BucketSeconds >= MinuteResolutionSeconds && query.BucketSeconds % MinuteResolutionSeconds == 0
            ? new MetricQuerySource(this.minuteBuckets, MinuteResolutionSeconds)
            : new MetricQuerySource(this.secondBuckets, 1);

    private void PruneIfDue(long observedSecond)
    {
        var lastPruned = Volatile.Read(ref this.lastPrunedSecond);
        if (observedSecond <= lastPruned + 30 ||
            Interlocked.CompareExchange(ref this.lastPrunedSecond, observedSecond, lastPruned) != lastPruned)
        {
            return;
        }

        this.secondBuckets.PruneBefore(observedSecond - SecondBucketRetentionSeconds - BucketRetentionBufferSeconds);
        this.minuteBuckets.PruneBefore(FloorToBucket(
            observedSecond - WorkThroughputQuery.MaximumWindowSeconds - BucketRetentionBufferSeconds,
            MinuteResolutionSeconds));
    }

    private static WorkThroughputQuery Normalize(WorkThroughputQuery? query)
    {
        var windowSeconds = Math.Clamp(
            query?.WindowSeconds ?? WorkThroughputQuery.DefaultWindowSeconds,
            1,
            WorkThroughputQuery.MaximumWindowSeconds);
        var bucketSeconds = Math.Clamp(
            query?.BucketSeconds ?? WorkThroughputQuery.DefaultBucketSeconds,
            1,
            windowSeconds);

        return new WorkThroughputQuery(windowSeconds, bucketSeconds);
    }

    private static long FloorToBucket(long second, int bucketSeconds)
        => second - PositiveMod(second, bucketSeconds);

    private static long PositiveMod(long value, int divisor)
        => ((value % divisor) + divisor) % divisor;

    private long? FindFirstBucketWithData(
        long fromBucketSecond,
        long toSecond,
        int bucketSeconds,
        MetricQuerySource source,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        for (var bucketSecond = fromBucketSecond; bucketSecond <= toSecond; bucketSecond += bucketSeconds)
        {
            var bucketEnd = Math.Min(toSecond, bucketSecond + bucketSeconds - 1);
            if (!this.Aggregate(source, bucketSecond, bucketEnd, definitionIds).IsEmpty)
            {
                return bucketSecond;
            }
        }

        return null;
    }

    private WorkMetricBucketSnapshot Aggregate(
        MetricQuerySource source,
        long bucketSecond,
        long bucketEnd,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var aggregate = new WorkMetricBucketSnapshot();
        if (definitionIds is null)
        {
            for (var second = FloorToBucket(bucketSecond, source.ResolutionSeconds);
                 second <= bucketEnd;
                 second += source.ResolutionSeconds)
            {
                if (source.Store.SystemBuckets.TryGetValue(second, out var bucket))
                {
                    aggregate.Add(bucket.Snapshot());
                }
            }

            return aggregate;
        }

        foreach (var definitionId in definitionIds)
        {
            if (!source.Store.DefinitionBuckets.TryGetValue(definitionId, out var definitionBuckets))
            {
                continue;
            }

            for (var second = FloorToBucket(bucketSecond, source.ResolutionSeconds);
                 second <= bucketEnd;
                 second += source.ResolutionSeconds)
            {
                if (definitionBuckets.TryGetValue(second, out var bucket))
                {
                    aggregate.Add(bucket.Snapshot());
                }
            }
        }

        return aggregate;
    }

    private static WorkThroughputBucket ToThroughputBucket(long second, WorkMetricBucketSnapshot aggregate)
        => new(
            DateTimeOffset.FromUnixTimeSeconds(second),
            ToInt32Saturated(aggregate.Started),
            ToInt32Saturated(aggregate.Completed),
            ToInt32Saturated(aggregate.Failed),
            ToInt32Saturated(aggregate.Canceled),
            aggregate.ExecutionCount == 0
                ? 0
                : TimeSpan.FromTicks(aggregate.ExecutionTicks / aggregate.ExecutionCount).TotalMilliseconds);

    private WorkThroughputLiveSummary CreateLiveSummary(
        long toSecond,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var aggregate = this.Aggregate(
            new MetricQuerySource(this.secondBuckets, 1),
            toSecond - LiveSummaryWindowSeconds + 1,
            toSecond,
            definitionIds);

        return new WorkThroughputLiveSummary(
            LiveSummaryWindowSeconds,
            aggregate.Started / (double)LiveSummaryWindowSeconds,
            aggregate.Completed / (double)LiveSummaryWindowSeconds,
            aggregate.Failed / (double)LiveSummaryWindowSeconds,
            aggregate.Canceled / (double)LiveSummaryWindowSeconds,
            aggregate.ExecutionCount == 0
                ? 0
                : TimeSpan.FromTicks(aggregate.ExecutionTicks / aggregate.ExecutionCount).TotalMilliseconds);
    }

    private static int ToInt32Saturated(long value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private sealed class MetricStore
    {
        public ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<long, WorkMetricBucket>> DefinitionBuckets { get; } = [];

        public ConcurrentDictionary<long, WorkMetricBucket> SystemBuckets { get; } = [];

        public WorkMetricBucket GetDefinitionBucket(WorkDefinitionId definitionId, long bucket)
            => this.DefinitionBuckets
                .GetOrAdd(definitionId, static _ => [])
                .GetOrAdd(bucket, static _ => new WorkMetricBucket());

        public WorkMetricBucket GetSystemBucket(long bucket)
            => this.SystemBuckets.GetOrAdd(bucket, static _ => new WorkMetricBucket());

        public void PruneBefore(long cutoff)
        {
            foreach (var bucket in this.SystemBuckets.Keys)
            {
                if (bucket < cutoff)
                {
                    this.SystemBuckets.TryRemove(bucket, out _);
                }
            }

            foreach (var (definitionId, buckets) in this.DefinitionBuckets)
            {
                foreach (var bucket in buckets.Keys)
                {
                    if (bucket < cutoff)
                    {
                        buckets.TryRemove(bucket, out _);
                    }
                }

                if (buckets.IsEmpty)
                {
                    this.DefinitionBuckets.TryRemove(definitionId, out _);
                }
            }
        }
    }

    private sealed record MetricQuerySource(MetricStore Store, int ResolutionSeconds);

    private sealed class WorkMetricBucket
    {
        private long started;
        private long completed;
        private long failed;
        private long canceled;
        private long executionCount;
        private long executionTicks;

        public void IncrementStarted()
            => Interlocked.Increment(ref this.started);

        public void IncrementCompleted()
            => Interlocked.Increment(ref this.completed);

        public void IncrementFailed()
            => Interlocked.Increment(ref this.failed);

        public void IncrementCanceled()
            => Interlocked.Increment(ref this.canceled);

        public void AddExecution(TimeSpan duration)
        {
            Interlocked.Increment(ref this.executionCount);
            Interlocked.Add(ref this.executionTicks, duration.Ticks);
        }

        public WorkMetricBucketSnapshot Snapshot()
            => new(
                Volatile.Read(ref this.started),
                Volatile.Read(ref this.completed),
                Volatile.Read(ref this.failed),
                Volatile.Read(ref this.canceled),
                Volatile.Read(ref this.executionCount),
                Volatile.Read(ref this.executionTicks));
    }

    private sealed class WorkMetricBucketSnapshot(
        long started = 0,
        long completed = 0,
        long failed = 0,
        long canceled = 0,
        long executionCount = 0,
        long executionTicks = 0)
    {
        public long Started { get; private set; } = started;

        public long Completed { get; private set; } = completed;

        public long Failed { get; private set; } = failed;

        public long Canceled { get; private set; } = canceled;

        public long ExecutionCount { get; private set; } = executionCount;

        public long ExecutionTicks { get; private set; } = executionTicks;

        public bool IsEmpty
            => this.Started == 0 &&
               this.Completed == 0 &&
               this.Failed == 0 &&
               this.Canceled == 0 &&
               this.ExecutionCount == 0;

        public void Add(WorkMetricBucketSnapshot bucket)
        {
            this.Started += bucket.Started;
            this.Completed += bucket.Completed;
            this.Failed += bucket.Failed;
            this.Canceled += bucket.Canceled;
            this.ExecutionCount += bucket.ExecutionCount;
            this.ExecutionTicks += bucket.ExecutionTicks;
        }
    }
}

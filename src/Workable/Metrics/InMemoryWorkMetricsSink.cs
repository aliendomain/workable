using System.Collections.Concurrent;

namespace Workable;

internal sealed class InMemoryWorkMetricsSink : IWorkMetricsSink
{
    private const int LiveSummaryWindowSeconds = 60;
    private const int SecondBucketRetentionSeconds = 15 * 60;
    private const int BucketRetentionBufferSeconds = 60;
    private const int MinuteResolutionSeconds = 60;
    private static readonly long[] ExecutionDurationHistogramUpperBoundsTicks =
    [
        0,
        TimeSpan.TicksPerMillisecond,
        2 * TimeSpan.TicksPerMillisecond,
        5 * TimeSpan.TicksPerMillisecond,
        10 * TimeSpan.TicksPerMillisecond,
        20 * TimeSpan.TicksPerMillisecond,
        50 * TimeSpan.TicksPerMillisecond,
        75 * TimeSpan.TicksPerMillisecond,
        100 * TimeSpan.TicksPerMillisecond,
        150 * TimeSpan.TicksPerMillisecond,
        200 * TimeSpan.TicksPerMillisecond,
        300 * TimeSpan.TicksPerMillisecond,
        500 * TimeSpan.TicksPerMillisecond,
        750 * TimeSpan.TicksPerMillisecond,
        TimeSpan.TicksPerSecond,
        1_500 * TimeSpan.TicksPerMillisecond,
        2 * TimeSpan.TicksPerSecond,
        3 * TimeSpan.TicksPerSecond,
        5 * TimeSpan.TicksPerSecond,
        7_500 * TimeSpan.TicksPerMillisecond,
        10 * TimeSpan.TicksPerSecond,
        15 * TimeSpan.TicksPerSecond,
        30 * TimeSpan.TicksPerSecond,
        TimeSpan.TicksPerMinute,
        5 * TimeSpan.TicksPerMinute,
    ];
    private static readonly int ExecutionDurationHistogramBucketCount =
        ExecutionDurationHistogramUpperBoundsTicks.Length + 1;

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
        WorkThroughputCriteria? query = null,
        IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var normalized = Normalize(query);
        var nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toSecond = GetLastClosedBucketEndSecond(nowSecond, normalized.BucketSeconds);
        var fromSecond = toSecond - normalized.WindowSeconds + 1;
        var requestedFirstBucketSecond = FloorToBucket(fromSecond, normalized.BucketSeconds);
        var source = this.GetQuerySource(normalized);
        var firstBucketSecond = FindFirstBucketWithData(
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
                CreateExecutionSummary(new WorkMetricBucketSnapshot()),
                this.CreateLiveSummary(nowSecond, definitionIds));
        }

        var summary = CreateExecutionSummary(Aggregate(
            source,
            requestedFirstBucketSecond,
            toSecond,
            definitionIds));
        var buckets = new List<WorkThroughputBucket>();

        for (var bucketSecond = firstBucketSecond.Value; bucketSecond <= toSecond; bucketSecond += normalized.BucketSeconds)
        {
            var bucketEnd = Math.Min(toSecond, bucketSecond + normalized.BucketSeconds - 1);
            var aggregate = Aggregate(source, bucketSecond, bucketEnd, definitionIds);
            buckets.Add(ToThroughputBucket(bucketSecond, aggregate));
        }

        return new WorkSystemThroughput(
            DateTimeOffset.FromUnixTimeSeconds(firstBucketSecond.Value),
            DateTimeOffset.FromUnixTimeSeconds(toSecond),
            normalized.WindowSeconds,
            normalized.BucketSeconds,
            buckets,
            summary,
            this.CreateLiveSummary(nowSecond, definitionIds));
    }

    public WorkSystemThroughputSummary GetThroughputSummary(
        WorkThroughputCriteria? query = null,
        IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var normalized = Normalize(query);
        var nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toSecond = GetLastClosedBucketEndSecond(nowSecond, normalized.BucketSeconds);
        var fromSecond = toSecond - normalized.WindowSeconds + 1;
        var requestedFirstBucketSecond = FloorToBucket(fromSecond, normalized.BucketSeconds);
        var source = this.GetQuerySource(normalized);
        return new WorkSystemThroughputSummary(
            normalized.WindowSeconds,
            CreateExecutionSummary(Aggregate(
                source,
                requestedFirstBucketSecond,
                toSecond,
                definitionIds)),
            this.CreateLiveSummary(nowSecond, definitionIds));
    }

    public void Clear()
    {
        this.secondBuckets.Clear();
        this.minuteBuckets.Clear();
        Volatile.Write(ref this.lastPrunedSecond, 0);
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

    private MetricQuerySource GetQuerySource(WorkThroughputCriteria query)
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
            observedSecond - WorkThroughputCriteria.MaximumWindowSeconds - BucketRetentionBufferSeconds,
            MinuteResolutionSeconds));
    }

    private static WorkThroughputCriteria Normalize(WorkThroughputCriteria? query)
    {
        var windowSeconds = Math.Clamp(
            query?.WindowSeconds ?? WorkThroughputCriteria.DefaultWindowSeconds,
            1,
            WorkThroughputCriteria.MaximumWindowSeconds);
        var bucketSeconds = Math.Clamp(
            query?.BucketSeconds ?? WorkThroughputCriteria.DefaultBucketSeconds,
            1,
            windowSeconds);

        return new WorkThroughputCriteria(windowSeconds, bucketSeconds);
    }

    private static long FloorToBucket(long second, int bucketSeconds)
        => second - PositiveMod(second, bucketSeconds);

    private static long GetLastClosedBucketEndSecond(long currentSecond, int bucketSeconds)
        => FloorToBucket(currentSecond, bucketSeconds) - 1;

    private static long PositiveMod(long value, int divisor)
        => ((value % divisor) + divisor) % divisor;

    private static long? FindFirstBucketWithData(
        long fromBucketSecond,
        long toSecond,
        int bucketSeconds,
        MetricQuerySource source,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        for (var bucketSecond = fromBucketSecond; bucketSecond <= toSecond; bucketSecond += bucketSeconds)
        {
            var bucketEnd = Math.Min(toSecond, bucketSecond + bucketSeconds - 1);
            if (!Aggregate(source, bucketSecond, bucketEnd, definitionIds).IsEmpty)
            {
                return bucketSecond;
            }
        }

        return null;
    }

    private static WorkMetricBucketSnapshot Aggregate(
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
    {
        var execution = CreateExecutionSummary(aggregate);
        return new WorkThroughputBucket(
            DateTimeOffset.FromUnixTimeSeconds(second),
            ToInt32Saturated(aggregate.Started),
            ToInt32Saturated(aggregate.Completed),
            ToInt32Saturated(aggregate.Failed),
            ToInt32Saturated(aggregate.Canceled),
            execution.AverageExecutionMilliseconds,
            execution.ExecutionCount,
            execution.SlowestExecutionMilliseconds,
            execution.P95ExecutionMilliseconds,
            execution.P99ExecutionMilliseconds);
    }

    private WorkThroughputLiveSummary CreateLiveSummary(
        long toSecond,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var aggregate = Aggregate(
            new MetricQuerySource(this.secondBuckets, 1),
            toSecond - LiveSummaryWindowSeconds + 1,
            toSecond,
            definitionIds);
        var execution = CreateExecutionSummary(aggregate);

        return new WorkThroughputLiveSummary(
            LiveSummaryWindowSeconds,
            aggregate.Started / (double)LiveSummaryWindowSeconds,
            aggregate.Completed / (double)LiveSummaryWindowSeconds,
            aggregate.Failed / (double)LiveSummaryWindowSeconds,
            aggregate.Canceled / (double)LiveSummaryWindowSeconds,
            (aggregate.Started - aggregate.Completed - aggregate.Failed - aggregate.Canceled) / (double)LiveSummaryWindowSeconds,
            execution.AverageExecutionMilliseconds,
            execution.ExecutionCount,
            execution.SlowestExecutionMilliseconds,
            execution.P95ExecutionMilliseconds,
            execution.P99ExecutionMilliseconds);
    }

    private static WorkThroughputExecutionSummary CreateExecutionSummary(WorkMetricBucketSnapshot aggregate)
        => new(
            ToInt32Saturated(aggregate.ExecutionCount),
            aggregate.ExecutionCount == 0
                ? 0
                : TimeSpan.FromTicks(aggregate.ExecutionTicks / aggregate.ExecutionCount).TotalMilliseconds,
            TimeSpan.FromTicks(aggregate.MaxExecutionTicks).TotalMilliseconds,
            GetExecutionPercentileMilliseconds(aggregate, 0.95),
            GetExecutionPercentileMilliseconds(aggregate, 0.99));

    private static int GetExecutionDurationHistogramBucket(long ticks)
    {
        for (var index = 0; index < ExecutionDurationHistogramUpperBoundsTicks.Length; index++)
        {
            if (ticks <= ExecutionDurationHistogramUpperBoundsTicks[index])
            {
                return index;
            }
        }

        return ExecutionDurationHistogramUpperBoundsTicks.Length;
    }

    private static double GetExecutionPercentileMilliseconds(
        WorkMetricBucketSnapshot aggregate,
        double percentile)
    {
        if (aggregate.ExecutionCount == 0)
        {
            return 0;
        }

        var target = aggregate.ExecutionCount * percentile;
        var observed = 0L;
        for (var index = 0; index < aggregate.ExecutionDurationHistogram.Length; index++)
        {
            var previousObserved = observed;
            observed += aggregate.ExecutionDurationHistogram[index];
            if (observed >= target)
            {
                return Math.Min(
                    GetExecutionDurationHistogramPercentileMilliseconds(index, previousObserved, observed, target),
                    TimeSpan.FromTicks(aggregate.MaxExecutionTicks).TotalMilliseconds);
            }
        }

        return TimeSpan.FromTicks(aggregate.MaxExecutionTicks).TotalMilliseconds;
    }

    private static double GetExecutionDurationHistogramPercentileMilliseconds(
        int index,
        long previousObserved,
        long observed,
        double target)
    {
        var bucketCount = observed - previousObserved;
        if (bucketCount <= 0)
        {
            return GetExecutionDurationHistogramUpperBoundMilliseconds(index);
        }

        var lowerBound = index == 0
            ? 0
            : GetExecutionDurationHistogramUpperBoundMilliseconds(index - 1);
        var upperBound = GetExecutionDurationHistogramUpperBoundMilliseconds(index);
        var bucketPosition = Math.Clamp((target - previousObserved) / bucketCount, 0, 1);

        return lowerBound + (upperBound - lowerBound) * bucketPosition;
    }

    private static double GetExecutionDurationHistogramUpperBoundMilliseconds(int index)
    {
        if (index < ExecutionDurationHistogramUpperBoundsTicks.Length)
        {
            return TimeSpan.FromTicks(ExecutionDurationHistogramUpperBoundsTicks[index]).TotalMilliseconds;
        }

        return TimeSpan.FromTicks(ExecutionDurationHistogramUpperBoundsTicks[^1]).TotalMilliseconds;
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

        public void Clear()
        {
            this.DefinitionBuckets.Clear();
            this.SystemBuckets.Clear();
        }

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
        private long maxExecutionTicks;
        private readonly long[] executionDurationHistogram = new long[ExecutionDurationHistogramBucketCount];

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
            var ticks = Math.Max(0, duration.Ticks);
            Interlocked.Increment(ref this.executionCount);
            Interlocked.Add(ref this.executionTicks, ticks);
            SetMaxExecutionTicks(ref this.maxExecutionTicks, ticks);
            Interlocked.Increment(ref this.executionDurationHistogram[GetExecutionDurationHistogramBucket(ticks)]);
        }

        public WorkMetricBucketSnapshot Snapshot()
            => new(
                Volatile.Read(ref this.started),
                Volatile.Read(ref this.completed),
                Volatile.Read(ref this.failed),
                Volatile.Read(ref this.canceled),
                Volatile.Read(ref this.executionCount),
                Volatile.Read(ref this.executionTicks),
                Volatile.Read(ref this.maxExecutionTicks),
                this.SnapshotExecutionDurationHistogram());

        private long[] SnapshotExecutionDurationHistogram()
        {
            var histogram = new long[ExecutionDurationHistogramBucketCount];
            for (var index = 0; index < histogram.Length; index++)
            {
                histogram[index] = Volatile.Read(ref this.executionDurationHistogram[index]);
            }

            return histogram;
        }

        private static void SetMaxExecutionTicks(ref long currentValue, long candidate)
        {
            var current = Volatile.Read(ref currentValue);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref currentValue, candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class WorkMetricBucketSnapshot(
        long started = 0,
        long completed = 0,
        long failed = 0,
        long canceled = 0,
        long executionCount = 0,
        long executionTicks = 0,
        long maxExecutionTicks = 0,
        long[]? executionDurationHistogram = null)
    {
        public long Started { get; private set; } = started;

        public long Completed { get; private set; } = completed;

        public long Failed { get; private set; } = failed;

        public long Canceled { get; private set; } = canceled;

        public long ExecutionCount { get; private set; } = executionCount;

        public long ExecutionTicks { get; private set; } = executionTicks;

        public long MaxExecutionTicks { get; private set; } = maxExecutionTicks;

        public long[] ExecutionDurationHistogram { get; } =
            CopyExecutionDurationHistogram(executionDurationHistogram);

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
            this.MaxExecutionTicks = Math.Max(this.MaxExecutionTicks, bucket.MaxExecutionTicks);
            for (var index = 0; index < this.ExecutionDurationHistogram.Length; index++)
            {
                this.ExecutionDurationHistogram[index] += bucket.ExecutionDurationHistogram[index];
            }
        }

        private static long[] CopyExecutionDurationHistogram(long[]? histogram)
        {
            var copy = new long[ExecutionDurationHistogramBucketCount];
            if (histogram is not null)
            {
                Array.Copy(histogram, copy, Math.Min(histogram.Length, copy.Length));
            }

            return copy;
        }
    }
}

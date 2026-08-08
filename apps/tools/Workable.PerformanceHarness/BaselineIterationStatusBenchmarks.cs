using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

/// <summary>
/// Compares the original front-removal replay buffer with indexed eviction, with and without payload-byte accounting.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class BaselineIterationStatusReplayBufferBenchmarks
{
    private const int OperationsPerInvocation = 4_096;
    private LegacyFrontRemovalBuffer legacy = null!;
    private IndexedEvictionBuffer indexedItemOnly = null!;
    private IndexedEvictionBuffer indexedWithPayloadBytes = null!;
    private int legacyValue;
    private int indexedItemOnlyValue;
    private int indexedWithPayloadBytesValue;

    [Params(256, 4_096)]
    public int Capacity { get; set; }

    [Params(0, 1_024)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var replayPayloadByteCapacity = checked(this.Capacity * Math.Max(1, this.PayloadBytes));
        this.legacy = new LegacyFrontRemovalBuffer(this.Capacity);
        this.indexedItemOnly = new IndexedEvictionBuffer(
            this.Capacity,
            replayPayloadByteCapacity,
            trackPayloadBytes: false);
        this.indexedWithPayloadBytes = new IndexedEvictionBuffer(
            this.Capacity,
            replayPayloadByteCapacity,
            trackPayloadBytes: true);
        for (var index = 0; index < this.Capacity; index++)
        {
            this.legacy.Append(index, this.PayloadBytes);
            this.indexedItemOnly.Append(index, this.PayloadBytes);
            this.indexedWithPayloadBytes.Append(index, this.PayloadBytes);
        }

        this.legacyValue = this.Capacity;
        this.indexedItemOnlyValue = this.Capacity;
        this.indexedWithPayloadBytesValue = this.Capacity;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvocation)]
    public int LegacyFrontRemoval()
    {
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            this.legacy.Append(this.legacyValue++, this.PayloadBytes);
        }

        return this.legacy.FirstValue;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int IndexedItemOnlyEviction()
    {
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            this.indexedItemOnly.Append(this.indexedItemOnlyValue++, this.PayloadBytes);
        }

        return this.indexedItemOnly.FirstValue;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int IndexedItemAndPayloadByteEviction()
    {
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            this.indexedWithPayloadBytes.Append(this.indexedWithPayloadBytesValue++, this.PayloadBytes);
        }

        return this.indexedWithPayloadBytes.FirstValue;
    }

    private readonly record struct BufferedValue(int Value, int PayloadBytes);

    private sealed class LegacyFrontRemovalBuffer(int itemCapacity)
    {
        private readonly List<BufferedValue> items = new(itemCapacity);

        public int FirstValue => this.items[0].Value;

        public void Append(int value, int payloadBytes)
        {
            this.items.Add(new BufferedValue(value, payloadBytes));
            if (this.items.Count > itemCapacity)
            {
                this.items.RemoveRange(0, this.items.Count - itemCapacity);
            }
        }
    }

    private sealed class IndexedEvictionBuffer(
        int itemCapacity,
        int replayPayloadByteCapacity,
        bool trackPayloadBytes)
    {
        private const int CompactionThreshold = 256;
        private readonly List<BufferedValue?> items = new(itemCapacity);
        private int firstIndex;
        private long retainedPayloadBytes;

        private int Count => this.items.Count - this.firstIndex;

        public int FirstValue => this.items[this.firstIndex]!.Value.Value;

        public void Append(int value, int payloadBytes)
        {
            this.items.Add(new BufferedValue(value, payloadBytes));
            if (trackPayloadBytes)
            {
                this.retainedPayloadBytes += payloadBytes;
            }

            while (this.Count > itemCapacity ||
                (trackPayloadBytes && this.retainedPayloadBytes > replayPayloadByteCapacity))
            {
                var removed = this.items[this.firstIndex]!.Value;
                this.items[this.firstIndex] = null;
                this.firstIndex++;
                if (trackPayloadBytes)
                {
                    this.retainedPayloadBytes -= removed.PayloadBytes;
                }
            }

            if (this.firstIndex >= CompactionThreshold && this.firstIndex >= this.items.Count / 2)
            {
                this.items.RemoveRange(0, this.firstIndex);
                this.firstIndex = 0;
            }
        }
    }
}

/// <summary>
/// Measures the actual status publication path with representative payload and fanout sizes.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BaselineIterationStatusPublishBenchmarks
{
    private WorkIterationStatusStream stream = null!;
    private WorkerIterationReference iteration;
    private WorkIterationStatusUpdate update = null!;
    private IWorkIterationStatusSubscription[] subscriptions = null!;

    // A JSON string with 32,766 ASCII characters serializes to the default 32,768-byte payload limit.
    [Params(0, 128, 4_096, 32_766)]
    public int PayloadCharacters { get; set; }

    [Params(0, 1, 16, 256)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            "status-benchmark",
            maximumSubscriptions: 512,
            maximumSubscriptionsPerIteration: 256);
        this.iteration = new WorkerIterationReference(WorkerId.New(), 1);
        this.stream.Begin(this.iteration, "benchmark.iteration-status.publish");
        this.update = this.PayloadCharacters == 0
            ? new WorkIterationStatusUpdate("benchmark.progress", Data: null)
            : WorkIterationStatusUpdate.FromValue(
                "benchmark.progress",
                new string('x', this.PayloadCharacters));
        this.subscriptions = Enumerable.Range(0, this.SubscriberCount)
            .Select(_ => this.stream.Subscribe(this.iteration))
            .ToArray();
    }

    [Benchmark]
    public void PublishStatus()
        => this.stream.Publish(
            this.iteration,
            "benchmark.iteration-status.publish",
            this.update);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var subscription in this.subscriptions)
        {
            subscription.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        this.stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

/// <summary>
/// Measures publication concurrency through one system-wide stream versus independent streams.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BaselineIterationStatusConcurrencyBenchmarks
{
    private const int OperationsPerInvocation = 4_096;
    private WorkIterationStatusStream sharedStream = null!;
    private WorkIterationStatusStream[] independentStreams = null!;
    private WorkerIterationReference[] sharedIterations = null!;
    private WorkerIterationReference[] independentIterations = null!;
    private WorkIterationStatusUpdate update = null!;

    [Params(1, 4, 16)]
    public int Parallelism { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.update = WorkIterationStatusUpdate.FromValue("benchmark.progress", new string('x', 128));
        this.sharedStream = new WorkIterationStatusStream(WorkSystemId.New(), "shared-status-benchmark");
        this.sharedIterations = new WorkerIterationReference[this.Parallelism];
        this.independentStreams = new WorkIterationStatusStream[this.Parallelism];
        this.independentIterations = new WorkerIterationReference[this.Parallelism];
        for (var lane = 0; lane < this.Parallelism; lane++)
        {
            this.sharedIterations[lane] = new WorkerIterationReference(WorkerId.New(), 1);
            this.sharedStream.Begin(this.sharedIterations[lane], "benchmark.iteration-status.concurrent");
            this.independentStreams[lane] = new WorkIterationStatusStream(
                WorkSystemId.New(),
                $"independent-status-benchmark-{lane}");
            this.independentIterations[lane] = new WorkerIterationReference(WorkerId.New(), 1);
            this.independentStreams[lane].Begin(
                this.independentIterations[lane],
                "benchmark.iteration-status.concurrent");
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvocation)]
    public int PublishAcrossOneSystemStream()
    {
        Parallel.For(0, this.Parallelism, lane =>
        {
            for (var operation = lane; operation < OperationsPerInvocation; operation += this.Parallelism)
            {
                this.sharedStream.Publish(
                    this.sharedIterations[lane],
                    "benchmark.iteration-status.concurrent",
                    this.update);
            }
        });
        return OperationsPerInvocation;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int PublishAcrossIndependentSystemStreams()
    {
        Parallel.For(0, this.Parallelism, lane =>
        {
            for (var operation = lane; operation < OperationsPerInvocation; operation += this.Parallelism)
            {
                this.independentStreams[lane].Publish(
                    this.independentIterations[lane],
                    "benchmark.iteration-status.concurrent",
                    this.update);
            }
        });
        return OperationsPerInvocation;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.sharedStream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var stream in this.independentStreams)
        {
            stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Measures completed-stream replay through the public subscription reader.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BaselineIterationStatusReplayBenchmarks
{
    private const int RetainedItems = 4_096;
    private WorkIterationStatusStream stream = null!;
    private WorkerIterationReference iteration;

    [Params(64, RetainedItems)]
    public int ReplayCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.stream = new WorkIterationStatusStream(WorkSystemId.New(), "status-replay-benchmark");
        this.iteration = new WorkerIterationReference(WorkerId.New(), 1);
        this.stream.Begin(this.iteration, "benchmark.iteration-status.replay");
        var update = new WorkIterationStatusUpdate("benchmark.progress", Data: null);
        for (var index = 0; index < RetainedItems; index++)
        {
            this.stream.Publish(this.iteration, "benchmark.iteration-status.replay", update);
        }

        this.stream.Complete(this.iteration);
    }

    [Benchmark]
    public async Task<long> ReplayStatuses()
    {
        var afterSequence = (long)RetainedItems - this.ReplayCount;
        await using var subscription = this.stream.Subscribe(this.iteration, afterSequence);
        var lastSequence = afterSequence;
        await foreach (var item in subscription.Read())
        {
            lastSequence = item.Sequence;
        }

        return lastSequence;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

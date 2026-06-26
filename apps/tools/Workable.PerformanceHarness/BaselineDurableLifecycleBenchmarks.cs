using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks representative durable queue lifecycle operations against SQL-backed storage.
/// </summary>
public class BaselineDurableLifecycleBenchmarks
{
    private DurableWorkBenchmarkSystem fixture = null!;
    private int nextWorkerIndex;

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture = DurableWorkBenchmarkSystem.Create().GetAwaiter().GetResult();
        this.nextWorkerIndex = 0;
    }

    [Benchmark(Baseline = true)]
    public Task<IWorkerHandle> QueueDurableQueuedWorker()
        => this.fixture.Session.Queue.Enqueue(
            this.fixture.DurableQueuedWorkName,
            WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));

    [Benchmark]
    public async Task<WorkCompletion> QueueDurableWorkerAndWaitForCompletion()
    {
        var handle = await this.fixture.Session.Queue.Enqueue(
            this.fixture.DurableFastWorkName,
            WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));
        return await handle.WaitForCompletion();
    }

    [Benchmark]
    public async Task<WorkActionOutcome> QueueThenStartDurableWorker()
    {
        var handle = await this.fixture.Session.Queue.Enqueue(
            this.fixture.DurableQueuedWorkName,
            WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));
        var worker = await this.fixture.WaitForWorker(
            handle.WorkerId ?? throw new InvalidOperationException("Expected durable worker id."));
        return await this.fixture.Session.Workers.Execute(
            worker.Version,
            WorkAction.Start);
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

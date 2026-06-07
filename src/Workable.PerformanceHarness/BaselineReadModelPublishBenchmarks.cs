using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
public sealed class BaselineReadModelPublishBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria flushCriteria = null!;
    private int nextWorkerIndex;

    public IEnumerable<int> WorkerCounts => BenchmarkScales.MutationWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    public int WorkerCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture = WorkableBenchmarkSystem.CreateQueued(this.WorkerCount).GetAwaiter().GetResult();
        this.flushCriteria = new WorkerCriteria(Take: WorkerCriteria.MaximumTake);
        this.nextWorkerIndex = this.WorkerCount;
    }

    [Benchmark]
    public async Task<WorkerQueryResult> FlushSingleWorkerUpdateIntoSnapshot()
    {
        await this.fixture.Session.Queue.Enqueue(
            this.fixture.Definitions[0].Name,
            WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));
        return await this.fixture.Session.Query.Workers(this.flushCriteria);
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

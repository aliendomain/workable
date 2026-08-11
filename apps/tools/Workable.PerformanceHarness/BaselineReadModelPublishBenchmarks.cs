using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Benchmarks the cost of publishing read-model updates after queue-time mutations.
/// </summary>
public class BaselineReadModelPublishBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria flushCriteria = null!;
    private int nextWorkerIndex;

    /// <summary>
    /// Gets the worker-count scale used by this benchmark.
    /// </summary>
    public IEnumerable<int> WorkerCounts => BenchmarkScales.MutationWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    /// <summary>
    /// Gets or sets the worker count for the current benchmark run.
    /// </summary>
    public int WorkerCount { get; set; }

    [IterationSetup]
    /// <summary>
    /// Creates the benchmark fixture for the current iteration.
    /// </summary>
    public void IterationSetup()
    {
        this.fixture = WorkableBenchmarkSystem.CreateQueued(this.WorkerCount).GetAwaiter().GetResult();
        this.flushCriteria = new WorkerCriteria(Take: WorkerCriteria.MaximumTake);
        this.nextWorkerIndex = this.WorkerCount;
    }

    [Benchmark]
    /// <summary>
    /// Measures queueing one additional worker and then flushing the updated read-model snapshot.
    /// </summary>
    public async Task<WorkerQueryResult> FlushSingleWorkerUpdateIntoSnapshot()
    {
        await this.fixture.Session.Queue.Enqueue(
            this.fixture.Definitions[0].Name,
            WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));
        await this.fixture.WaitForReadModelToSettle();
        return await this.fixture.Session.Query.Workers(this.flushCriteria);
    }

    [IterationCleanup]
    /// <summary>
    /// Disposes the benchmark fixture for the current iteration.
    /// </summary>
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Benchmarks worker-query behavior against very large datasets.
/// </summary>
public class StressMillionWorkerQueryBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria broadFirstPage = null!;
    private WorkerCriteria indexedIdentifierFirstPage = null!;

    [ParamsSource(nameof(WorkerCounts))]
    /// <summary>
    /// Gets or sets the worker count for the current benchmark run.
    /// </summary>
    public int WorkerCount { get; set; }

    /// <summary>
    /// Gets the million-worker scale used by this benchmark.
    /// </summary>
    public IEnumerable<int> WorkerCounts => BenchmarkScales.MillionWorkerCounts;

    [GlobalSetup]
    /// <summary>
    /// Creates the benchmark fixture and reusable query criteria.
    /// </summary>
    public void GlobalSetup()
    {
        this.fixture = WorkableBenchmarkSystem.CreateQueued(this.WorkerCount).GetAwaiter().GetResult();
        this.broadFirstPage = new WorkerCriteria(Take: WorkerCriteria.MaximumTake);
        this.indexedIdentifierFirstPage = new WorkerCriteria(
            Identifier: WorkableBenchmarkSystem.HotIdentifier,
            Take: WorkerCriteria.MaximumTake);
    }

    [Benchmark(Baseline = true)]
    /// <summary>
    /// Measures the broad first-page worker query without a selective key filter.
    /// </summary>
    public Task<WorkerQueryResult> BroadFirstPage()
        => this.fixture.Session.Query.Workers(this.broadFirstPage);

    [Benchmark]
    /// <summary>
    /// Measures the first-page worker query using the benchmark fixture's hot identifier filter.
    /// </summary>
    public Task<WorkerQueryResult> IndexedIdentifierFirstPage()
        => this.fixture.Session.Query.Workers(this.indexedIdentifierFirstPage);

    [GlobalCleanup]
    /// <summary>
    /// Disposes the benchmark fixture.
    /// </summary>
    public void GlobalCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Benchmarks representative worker-query patterns against a normally sized dataset.
/// </summary>
public class BaselineWorkerQueryBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria broadFirstPage = null!;
    private WorkerCriteria indexedIdentifierFirstPage = null!;
    private WorkerKeyTypeCriteria identifierKeyTypeFacet = null!;

    /// <summary>
    /// Gets the worker-count scale used by this benchmark.
    /// </summary>
    public IEnumerable<int> WorkerCounts => BenchmarkScales.QueryWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    /// <summary>
    /// Gets or sets the worker count for the current benchmark run.
    /// </summary>
    public int WorkerCount { get; set; }

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
        this.identifierKeyTypeFacet = new WorkerKeyTypeCriteria(
            Kind: WorkKeyKind.Identifier,
            Type: WorkableBenchmarkSystem.HotIdentifier.Type,
            Take: 50);
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

    [Benchmark]
    /// <summary>
    /// Measures loading identifier key-type facets for the benchmark fixture's hot identifier type.
    /// </summary>
    public Task<WorkerKeyTypeQueryResult> IdentifierKeyTypeFacet()
        => this.fixture.Session.Query.WorkerKeyTypes(this.identifierKeyTypeFacet);

    [GlobalCleanup]
    /// <summary>
    /// Disposes the benchmark fixture.
    /// </summary>
    public void GlobalCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

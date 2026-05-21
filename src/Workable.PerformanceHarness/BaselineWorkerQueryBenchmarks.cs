using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
public sealed class BaselineWorkerQueryBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria broadFirstPage = null!;
    private WorkerCriteria indexedIdentifierFirstPage = null!;
    private WorkerKeyTypeCriteria identifierKeyTypeFacet = null!;

    public IEnumerable<int> WorkerCounts => BenchmarkScales.QueryWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    public int WorkerCount { get; set; }

    [GlobalSetup]
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
    public Task<WorkerQueryResult> BroadFirstPage()
        => this.fixture.Session.Query.Workers(this.broadFirstPage);

    [Benchmark]
    public Task<WorkerQueryResult> IndexedIdentifierFirstPage()
        => this.fixture.Session.Query.Workers(this.indexedIdentifierFirstPage);

    [Benchmark]
    public Task<WorkerKeyTypeQueryResult> IdentifierKeyTypeFacet()
        => this.fixture.Session.Query.WorkerKeyTypes(this.identifierKeyTypeFacet);

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

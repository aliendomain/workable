using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
public sealed class StressMillionWorkerQueryBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria broadFirstPage = null!;
    private WorkerCriteria indexedIdentifierFirstPage = null!;

    [ParamsSource(nameof(WorkerCounts))]
    public int WorkerCount { get; set; }

    public IEnumerable<int> WorkerCounts => BenchmarkScales.MillionWorkerCounts;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.fixture = WorkableBenchmarkSystem.CreateQueued(this.WorkerCount).GetAwaiter().GetResult();
        this.broadFirstPage = new WorkerCriteria(Take: WorkerCriteria.MaximumTake);
        this.indexedIdentifierFirstPage = new WorkerCriteria(
            Identifier: WorkableBenchmarkSystem.HotIdentifier,
            Take: WorkerCriteria.MaximumTake);
    }

    [Benchmark(Baseline = true)]
    public Task<WorkerQueryResult> BroadFirstPage()
        => this.fixture.Session.Query.Workers(this.broadFirstPage);

    [Benchmark]
    public Task<WorkerQueryResult> IndexedIdentifierFirstPage()
        => this.fixture.Session.Query.Workers(this.indexedIdentifierFirstPage);

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

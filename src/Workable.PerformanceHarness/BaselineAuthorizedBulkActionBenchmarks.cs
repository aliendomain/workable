using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
public sealed class BaselineAuthorizedBulkActionBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;

    public IEnumerable<int> WorkerCounts => BenchmarkScales.BulkActionWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    public int WorkerCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture = WorkableBenchmarkSystem
            .CreateQueued(
                this.WorkerCount,
                requiresAuthorization: true,
                includeUnauthorizedDefinition: true)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public Task<WorkerBulkActionOutcome> ExecuteAllCancelAuthorizedQueuedWorkers()
        => this.fixture.Session.Workers.ExecuteAll(WorkAction.Cancel);

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

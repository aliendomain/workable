using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks authorized bulk worker actions against queued workers.
/// </summary>
public class BaselineAuthorizedBulkActionBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;

    /// <summary>
    /// Gets the worker-count scale used by this benchmark.
    /// </summary>
    public IEnumerable<int> WorkerCounts => BenchmarkScales.BulkActionWorkerCounts;

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
        this.fixture = WorkableBenchmarkSystem
            .CreateQueued(
                this.WorkerCount,
                requiresAuthorization: true,
                includeUnauthorizedDefinition: true)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    /// <summary>
    /// Measures canceling all authorized queued workers through the bulk-action surface.
    /// </summary>
    public Task<WorkerBulkActionOutcome> ExecuteAllCancelAuthorizedQueuedWorkers()
        => this.fixture.Session.Workers.ExecuteAll(WorkAction.Cancel);

    [IterationCleanup]
    /// <summary>
    /// Disposes the benchmark fixture for the current iteration.
    /// </summary>
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

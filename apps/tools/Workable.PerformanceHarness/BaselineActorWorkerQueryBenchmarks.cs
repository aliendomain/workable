using BenchmarkDotNet.Attributes;
using Workable;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Benchmarks actor-indexed worker queries at broad and selective match ratios.
/// </summary>
public class BaselineActorWorkerQueryBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;
    private WorkerCriteria actorCriteria = null!;

    /// <summary>
    /// Gets the worker-count scale used by this benchmark.
    /// </summary>
    public IEnumerable<int> WorkerCounts => BenchmarkScales.QueryWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    /// <summary>
    /// Gets or sets the number of workers in the read model.
    /// </summary>
    public int WorkerCount { get; set; }

    [Params(1, 100)]
    /// <summary>
    /// Gets or sets the number of evenly distributed originating actors.
    /// One actor is a 100% match; one hundred actors is approximately a 1% match.
    /// </summary>
    public int ActorCount { get; set; }

    [GlobalSetup]
    /// <summary>
    /// Creates workers evenly across the configured number of actors.
    /// </summary>
    public void GlobalSetup()
    {
        this.fixture = WorkableBenchmarkSystem.CreateQueued(
                this.WorkerCount,
                actorCount: this.ActorCount)
            .GetAwaiter()
            .GetResult();
        this.actorCriteria = new WorkerCriteria(
            ActorId: WorkableBenchmarkSystem.ActorId(0),
            Take: WorkerCriteria.MaximumTake);
    }

    [Benchmark]
    /// <summary>
    /// Measures an exact actor-index query at the configured selectivity.
    /// </summary>
    public Task<WorkerQueryResult> ActorFirstPage()
        => this.fixture.Session.Query.Workers(this.actorCriteria);

    [GlobalCleanup]
    /// <summary>
    /// Disposes the benchmark fixture.
    /// </summary>
    public void GlobalCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

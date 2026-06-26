using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks larger SQL-backed batches to expose durable queue memory and latency regressions.
/// </summary>
public class BaselineDurableSoakBenchmarks
{
    private DurableWorkBenchmarkSystem fixture = null!;
    private int nextWorkerIndex;

    public IEnumerable<int> WorkerCounts => BenchmarkScales.DurableSoakWorkerCounts;

    [ParamsSource(nameof(WorkerCounts))]
    public int WorkerCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture = DurableWorkBenchmarkSystem.Create().GetAwaiter().GetResult();
        this.nextWorkerIndex = 0;
    }

    [Benchmark(Baseline = true)]
    public async Task<int> QueueCompleteAndQueryDurableBatch()
    {
        var completions = new Task<WorkCompletion>[this.WorkerCount];
        for (var index = 0; index < this.WorkerCount; index++)
        {
            var handle = await this.fixture.Session.Queue.Enqueue(
                this.fixture.DurableFastWorkName,
                WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));
            completions[index] = handle.WaitForCompletion();
        }

        var results = await Task.WhenAll(completions);
        var summary = await this.fixture.Session.Query.WorkerStatusSummary(
            new WorkerCriteria(DefinitionName: this.fixture.DurableFastWorkName));
        return results.Count(result => result.IsCompletedSuccessfully) + summary.Total;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

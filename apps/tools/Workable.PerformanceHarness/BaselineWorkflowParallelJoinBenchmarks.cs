using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Benchmarks the overhead of parallel branch dispatch plus join bookkeeping.
/// </summary>
public class BaselineWorkflowParallelJoinBenchmarks
{
    private const int WorkflowOperationsPerInvoke = 4_096;
    private WorkflowBenchmarkSystem fixture = null!;

    public IEnumerable<int> BranchCounts => BenchmarkScales.WorkflowBranchCounts;

    [ParamsSource(nameof(BranchCounts))]
    public int BranchCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
        => this.fixture = WorkflowBenchmarkSystem.Create(this.BranchCount).GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = WorkflowOperationsPerInvoke)]
    public async Task<WorkflowRunStatus> StartParallelJoinWorkflowAndWaitForCompletion()
    {
        WorkflowRunStatus status = default;

        for (var i = 0; i < WorkflowOperationsPerInvoke; i++)
        {
            status = await WorkflowBenchmarkReflection.StartAndWaitForCompletion(
                this.fixture.System,
                "perf.workflow.parallel",
                this.fixture.RequestContext);
        }

        return status;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Benchmarks the overhead of starting a simple single-dispatch workflow.
/// </summary>
public class BaselineWorkflowDispatchBenchmarks
{
    private const int WorkflowOperationsPerInvoke = 4_096;
    private WorkflowBenchmarkSystem fixture = null!;

    [IterationSetup]
    public void IterationSetup()
        => this.fixture = WorkflowBenchmarkSystem.Create(branchCount: 1).GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = WorkflowOperationsPerInvoke)]
    public async Task<WorkflowRunStatus> StartDispatchWorkflowAndWaitForCompletion()
    {
        WorkflowRunStatus status = default;

        for (var i = 0; i < WorkflowOperationsPerInvoke; i++)
        {
            status = await WorkflowBenchmarkReflection.StartAndWaitForCompletion(
                this.fixture.System,
                "perf.workflow.dispatch",
                this.fixture.RequestContext);
        }

        return status;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

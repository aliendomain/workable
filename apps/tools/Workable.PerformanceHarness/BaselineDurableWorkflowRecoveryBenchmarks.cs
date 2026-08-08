using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks startup recovery for interrupted durable workflow runs.
/// </summary>
public class BaselineDurableWorkflowRecoveryBenchmarks
{
    private Guid runId;
    private DurableWorkflowBenchmarkSystem? recovered;
    private string connectionString = string.Empty;
    private string schemaName = string.Empty;

    [IterationSetup]
    public void IterationSetup()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = DurableWorkflowBenchmarkSystem.Create(
            branchCount: 1,
            childExecutorFactory: _ => async (_, _, cancellationToken) =>
            {
                childStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorkExecutionResult.Success();
            }).GetAwaiter().GetResult();
        this.connectionString = first.ConnectionString;
        this.schemaName = first.DurabilitySchemaName;

        this.runId = WorkflowBenchmarkReflection.Start(
            first.System,
            "perf.workflow.durable.dispatch",
            BenchmarkRequestContexts.CreateAnonymous("Prepare durable workflow recovery benchmark.")).GetAwaiter().GetResult();
        DurableWorkflowBenchmarkSystem.WaitForSignal(
            childStarted.Task,
            "durable workflow child to start").GetAwaiter().GetResult();
        DurableWorkflowBenchmarkSystem.WaitForDurableState(
            first.ConnectionString,
            first.DurabilitySchemaName,
            counts => counts.WorkflowRuns >= 1 && counts.WorkEntries >= 1).GetAwaiter().GetResult();
        first.DisposeAsync().AsTask().GetAwaiter().GetResult();
        DurableWorkflowBenchmarkSystem.ExpireDurableWorkerLeases(
            this.connectionString,
            this.schemaName).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<WorkflowRunStatus> RecoverInterruptedDispatchWorkflowOnStartup()
    {
        this.recovered = await DurableWorkflowBenchmarkSystem.Create(
            branchCount: 1,
            childExecutorFactory: _ => (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            resetStore: false);
        return await DurableWorkflowBenchmarkSystem.WaitForFinalStatus(this.recovered.System, this.runId);
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.recovered?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

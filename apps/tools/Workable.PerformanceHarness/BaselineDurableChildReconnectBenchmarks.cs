using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks recovery paths that reconnect durable child workers to partially completed workflow runs.
/// </summary>
public class BaselineDurableChildReconnectBenchmarks
{
    private Guid runId;
    private DurableWorkflowBenchmarkSystem? recovered;
    private string connectionString = string.Empty;
    private string schemaName = string.Empty;

    public IEnumerable<int> BranchCounts => BenchmarkScales.RecoveryBranchCounts;

    [ParamsSource(nameof(BranchCounts))]
    public int BranchCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        var blockedBranches = 0;
        var allBlockedChildrenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = DurableWorkflowBenchmarkSystem.Create(
            this.BranchCount,
            childExecutorFactory: index =>
            {
                if (index == 0)
                {
                    return (_, _, _) => Task.FromResult(WorkExecutionResult.Success());
                }

                return async (_, _, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref blockedBranches) == Math.Max(1, this.BranchCount) - 1)
                    {
                        allBlockedChildrenStarted.TrySetResult();
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                };
            }).GetAwaiter().GetResult();
        this.connectionString = first.ConnectionString;
        this.schemaName = first.DurabilitySchemaName;

        this.runId = WorkflowBenchmarkReflection.Start(
            first.System,
            "perf.workflow.durable.parallel",
            BenchmarkRequestContexts.CreateAnonymous("Prepare durable child reconnect benchmark."));
        allBlockedChildrenStarted.Task.GetAwaiter().GetResult();
        DurableWorkflowBenchmarkSystem.WaitForDurableState(
            first.ConnectionString,
            first.DurabilitySchemaName,
            counts => counts.WorkflowRuns >= 1 && counts.WorkEntries >= Math.Max(1, this.BranchCount)).GetAwaiter().GetResult();
        first.DisposeAsync().AsTask().GetAwaiter().GetResult();
        DurableWorkflowBenchmarkSystem.ExpireDurableWorkerLeases(
            this.connectionString,
            this.schemaName).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<int> ResumeOnlyIncompleteParallelBranchesOnStartup()
    {
        var resumedChildren = 0;
        this.recovered = await DurableWorkflowBenchmarkSystem.Create(
            this.BranchCount,
            childExecutorFactory: _ => (_, _, _) =>
            {
                Interlocked.Increment(ref resumedChildren);
                return Task.FromResult(WorkExecutionResult.Success());
            },
            resetStore: false);
        var status = await DurableWorkflowBenchmarkSystem.WaitForFinalStatus(this.recovered.System, this.runId);
        if (status != WorkflowRunStatus.Completed)
        {
            throw new InvalidOperationException($"Expected recovered workflow to complete, but it settled as '{status}'.");
        }

        return resumedChildren;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.recovered?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

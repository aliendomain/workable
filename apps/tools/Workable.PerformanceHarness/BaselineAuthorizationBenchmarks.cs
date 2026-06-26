using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks authorization checks across queue, query, and workflow operation paths.
/// </summary>
public class BaselineAuthorizationBenchmarks
{
    private WorkableBenchmarkSystem workerFixture = null!;
    private WorkflowBenchmarkSystem workflowFixture = null!;
    private int nextWorkerIndex;

    public IEnumerable<int> DefinitionCounts => BenchmarkScales.AuthorizationDefinitionCounts;

    [ParamsSource(nameof(DefinitionCounts))]
    public int DefinitionCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        this.workerFixture = WorkableBenchmarkSystem.CreateQueued(
                workerCount: 100,
                requiresAuthorization: true,
                definitionCount: this.DefinitionCount,
                includeUnauthorizedDefinition: true)
            .GetAwaiter()
            .GetResult();
        this.workflowFixture = WorkflowBenchmarkSystem.Create(
                branchCount: 4,
                requiresAuthorization: true)
            .GetAwaiter()
            .GetResult();
        this.nextWorkerIndex = 100;
    }

    [Benchmark(Baseline = true)]
    public Task<IWorkerHandle> QueueAuthorizedWorker()
        => this.workerFixture.Session.Queue.Enqueue(
            this.workerFixture.Definitions[0].Name,
            WorkableBenchmarkSystem.CreateInput(this.nextWorkerIndex++));

    [Benchmark]
    public Task<WorkerQueryResult> QueryAuthorizedWorkers()
        => this.workerFixture.Session.Query.Workers(new WorkerCriteria(Take: WorkerCriteria.MaximumTake));

    [Benchmark]
    public Task<WorkflowRunStatus> StartAuthorizedWorkflow()
        => WorkflowBenchmarkReflection.StartAndWaitForCompletion(
            this.workflowFixture.System,
            "perf.workflow.parallel",
            this.workflowFixture.RequestContext);

    [IterationCleanup]
    public void IterationCleanup()
    {
        this.workflowFixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.workerFixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks workflow control propagation across a large set of outstanding child workers.
/// </summary>
public class BaselineWorkflowChildControlBenchmarks
{
    private const int ChildCount = 32_768;
    private ServiceProvider provider = null!;
    private InMemoryWorkSystem system = null!;
    private IWorkSystemSession session = null!;
    private WorkflowRunState run = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        var child = WorkDefinition.Create("perf.workflow.control.child", category: "Perf:Workflow");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                child,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.DoNotStart());
            builder.AddWorkflow(
                WorkflowDefinition.Create("perf.workflow.control"),
                workflow => workflow.DispatchWork("dispatch", child));
        });

        this.provider = services.BuildServiceProvider();
        this.system = (InMemoryWorkSystem)this.provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var requestContext = BenchmarkRequestContexts.CreateAnonymous(
            "Propagate workflow control in a performance benchmark.");
        this.system.Start(requestContext).GetAwaiter().GetResult();
        this.session = this.system.CreateSession(requestContext).AsTask().GetAwaiter().GetResult();

        var workflow = this.system.Workflows.TryGet("perf.workflow.control", out var registeredWorkflow)
            ? registeredWorkflow
            : throw new InvalidOperationException("The workflow control benchmark workflow was not registered.");
        this.run = WorkflowRunState.Create(workflow, requestContext);
        var input = WorkflowExecutionSupport.AddWorkflowRunIdentifier(
            input: null,
            this.run.Id);
        var catalog = (WorkSystemCatalog)this.system.Catalog;
        var registeredChild = catalog.TryGetWork(child.Name, out var registeredWork)
            ? registeredWork
            : throw new InvalidOperationException("The workflow control benchmark child was not registered.");
        var provenance = new WorkflowProvenance(this.run.Id, workflow.Definition.Name, "dispatch");
        var workerIds = new WorkerId[ChildCount];
        for (var index = 0; index < workerIds.Length; index++)
        {
            var handle = this.system.WorkerOperations.CreateWorker(
                registeredChild,
                input,
                options: null,
                requestContext,
                CancellationToken.None,
                provenance).GetAwaiter().GetResult();
            workerIds[index] = handle.WorkerId ?? throw new InvalidOperationException(
                "The workflow control benchmark child was not queued.");
        }

        this.run.MarkStepCompleted("dispatch", workerIds);
        WaitForReadModelToSettle(this.system);
    }

    [Benchmark(OperationsPerInvoke = ChildCount)]
    public async Task PauseOutstandingChildren()
    {
        await WorkflowExecutionSupport.PauseOutstandingChildren(
            this.run,
            this.session,
            this.system.WorkerOperations.GetAuthoritative,
            CancellationToken.None,
            this.system.WorkerOperations);
        WaitForReadModelToSettle(this.system);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        try
        {
            this.system.Stop(this.session is WorkSystemSession systemSession
                ? systemSession.RequestContext
                : WorkRequestContext.Create(WorkInvocationChannel.InProcess)).GetAwaiter().GetResult();
        }
        finally
        {
            this.provider.Dispose();
        }
    }

    private static void WaitForReadModelToSettle(InMemoryWorkSystem system)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var diagnostics = system.Diagnostics.ReadModel;
            if (diagnostics.PendingUpdateCount == 0 &&
                diagnostics.AppliedSequence == diagnostics.EnqueuedSequence)
            {
                return;
            }

            if (timeout.Elapsed >= TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("The workflow control benchmark read model did not settle.");
            }

            Thread.Sleep(1);
        }
    }
}

using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks MCP tool routing for worker and workflow operations.
/// </summary>
public class BaselineMcpBenchmarks
{
    private TransportBenchmarkHost host = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        this.host = TransportBenchmarkHost.Create().GetAwaiter().GetResult();
        this.host.Gates.Reset();
    }

    [Benchmark(Baseline = true)]
    public Task<WorkableMcpToolResult> StartWorkflowThroughMcp()
        => this.CallTool(
            "workable_start_workflow",
            """{"name":"perf.transport.workflow.fast","waitForCompletion":true,"description":"Start workflow through MCP benchmark."}""");

    [Benchmark]
    public async Task<WorkableMcpToolResult> StartQueuedWorkerThroughMcp()
    {
        var worker = await this.host.QueueDirectQueuedWorker();
        return await this.CallTool(
            "workable_start_worker",
            $$"""{"workerId":"{{worker.WorkerId.Value:D}}","revision":{{worker.Revision}},"description":"Start worker through MCP benchmark."}""");
    }

    [Benchmark]
    public async Task<WorkableMcpToolResult> CancelRunningWorkerThroughMcp()
    {
        var worker = await this.host.QueueDirectRunningWorker();
        return await this.CallTool(
            "workable_cancel_worker",
            $$"""{"workerId":"{{worker.WorkerId.Value:D}}","revision":{{worker.Revision}},"description":"Cancel worker through MCP benchmark."}""");
    }

    [Benchmark]
    public async Task<WorkableMcpToolResult> StopWorkflowThroughMcp()
    {
        var runId = await this.host.StartHttpWorkflow("perf.transport.workflow.stop");
        await this.host.Gates.StopWorkflowChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await this.CallTool(
            "workable_stop_workflow",
            $$"""{"runId":"{{runId.Value:D}}","description":"Stop workflow through MCP benchmark."}""");
        this.host.Gates.StopWorkflowRelease.TrySetResult();
        return result;
    }

    [Benchmark]
    public async Task<WorkableMcpToolResult> CancelWorkflowThroughMcp()
    {
        var runId = await this.host.StartHttpWorkflow("perf.transport.workflow.cancel");
        await this.host.Gates.CancelWorkflowChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await this.CallTool(
            "workable_cancel_workflow",
            $$"""{"runId":"{{runId.Value:D}}","description":"Cancel workflow through MCP benchmark."}""");
        this.host.Gates.CancelWorkflowRelease.TrySetResult();
        return result;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.host.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<WorkableMcpToolResult> CallTool(string toolName, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await this.host.Router.CallTool(
            toolName,
            document.RootElement,
            options: null,
            systemName: null,
            requestContext: this.host.CreateTransportRequestContext("Invoke MCP performance benchmark."));
    }
}

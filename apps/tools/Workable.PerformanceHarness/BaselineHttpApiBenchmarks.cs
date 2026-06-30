using BenchmarkDotNet.Attributes;
using System.Net.Http.Json;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks end-to-end HTTP API request paths for worker and workflow operations.
/// </summary>
public class BaselineHttpApiBenchmarks
{
    private TransportBenchmarkHost host = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        this.host = TransportBenchmarkHost.Create().GetAwaiter().GetResult();
        this.host.Gates.Reset();
    }

    [Benchmark(Baseline = true)]
    public async Task<System.Net.HttpStatusCode> QueueWorkerOverHttp()
    {
        var response = await this.host.Client.PostAsJsonAsync(
            "/workable/work/perf.transport.queued",
            new { description = "Queue worker over HTTP benchmark." });
        return response.StatusCode;
    }

    [Benchmark]
    public async Task<System.Net.HttpStatusCode> StartQueuedWorkerOverHttp()
    {
        var worker = await this.host.QueueHttpQueuedWorker();
        var response = await this.host.Client.PostAsJsonAsync(
            $"/workable/workers/{worker.WorkerId.Value:D}/actions/start",
            new
            {
                revision = worker.Revision,
                description = "Start queued worker over HTTP benchmark.",
            });
        return response.StatusCode;
    }

    [Benchmark]
    public async Task<System.Net.HttpStatusCode> CancelRunningWorkerOverHttp()
    {
        var workerVersion = await this.host.QueueDirectRunningWorker();
        var response = await this.host.Client.PostAsJsonAsync(
            $"/workable/workers/{workerVersion.WorkerId.Value:D}/actions/cancel",
            new
            {
                revision = workerVersion.Revision,
                description = "Cancel running worker over HTTP benchmark.",
            });
        return response.StatusCode;
    }

    [Benchmark]
    public async Task<System.Net.HttpStatusCode> StartWorkflowOverHttp()
    {
        var response = await this.host.Client.PostAsJsonAsync(
            "/workable/workflows/perf.transport.workflow.fast",
            new
            {
                completion = "waitForCompletion",
                description = "Start workflow over HTTP benchmark.",
            });
        return response.StatusCode;
    }

    [Benchmark]
    public async Task<System.Net.HttpStatusCode> StopWorkflowOverHttp()
    {
        var runId = await this.host.StartHttpWorkflow("perf.transport.workflow.stop");
        await this.host.Gates.StopWorkflowChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var response = await this.host.Client.PostAsJsonAsync(
            $"/workable/workflow-runs/{runId.Value:D}/actions/stop",
            new { description = "Stop workflow over HTTP benchmark." });
        this.host.Gates.StopWorkflowRelease.TrySetResult();
        return response.StatusCode;
    }

    [Benchmark]
    public async Task<System.Net.HttpStatusCode> CancelWorkflowOverHttp()
    {
        var runId = await this.host.StartHttpWorkflow("perf.transport.workflow.cancel");
        await this.host.Gates.CancelWorkflowChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var response = await this.host.Client.PostAsJsonAsync(
            $"/workable/workflow-runs/{runId.Value:D}/actions/cancel",
            new { description = "Cancel workflow over HTTP benchmark." });
        this.host.Gates.CancelWorkflowRelease.TrySetResult();
        return response.StatusCode;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.host.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

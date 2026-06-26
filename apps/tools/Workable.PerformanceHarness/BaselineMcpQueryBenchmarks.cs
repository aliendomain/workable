using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks MCP query tools over a seeded worker set.
/// </summary>
public class BaselineMcpQueryBenchmarks
{
    private const int SeedWorkerCount = 64;

    private TransportBenchmarkHost host = null!;
    private WorkerId workerId;

    [IterationSetup]
    public void IterationSetup()
    {
        this.host = TransportBenchmarkHost.Create().GetAwaiter().GetResult();
        this.host.Gates.Reset();
        this.workerId = this.host.SeedQueuedWorkers(SeedWorkerCount).GetAwaiter().GetResult()[0];
    }

    [Benchmark(Baseline = true)]
    public async Task<int> QueryWorkersThroughMcp()
    {
        var result = await this.CallTool(
            "workable_query_workers",
            $$"""{"workName":"perf.transport.queued","states":["Queued"],"take":{{SeedWorkerCount}}}""");
        var json = JsonNode.Parse(result.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected MCP query worker response.");
        return json["totalCount"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Expected MCP worker total count.");
    }

    [Benchmark]
    public async Task<int> GetWorkerStatusSummaryThroughMcp()
    {
        var result = await this.CallTool(
            "workable_get_worker_status_summary",
            """{"workName":"perf.transport.queued","states":["Queued"]}""");
        var json = JsonNode.Parse(result.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected MCP worker status summary response.");
        return json["total"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Expected MCP worker total count.");
    }

    [Benchmark]
    public async Task<bool> GetWorkerThroughMcp()
    {
        var result = await this.CallTool(
            "workable_get_worker",
            $$"""{"workerId":"{{this.workerId.Value:D}}"}""");
        var json = JsonNode.Parse(result.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected MCP get worker response.");
        return json["found"]?.GetValue<bool>()
            ?? throw new InvalidOperationException("Expected MCP worker presence flag.");
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.host.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<WorkableMcpToolResult> CallTool(string toolName, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = await this.host.Router.CallTool(
            toolName,
            document.RootElement,
            options: null,
            systemName: null,
            requestContext: this.host.CreateTransportRequestContext("Invoke MCP query performance benchmark."));
        if (result.IsError)
        {
            throw new InvalidOperationException(result.Json);
        }

        return result;
    }
}

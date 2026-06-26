using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks end-to-end HTTP query routes over a seeded worker set.
/// </summary>
public class BaselineHttpQueryBenchmarks
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
    public async Task<long> GetWorkerOverHttp()
    {
        var response = await this.host.Client.GetAsync($"/workable/workers/{this.workerId.Value:D}");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected worker JSON response.");
        return json["revision"]?.GetValue<long>()
            ?? throw new InvalidOperationException("Expected worker revision.");
    }

    [Benchmark]
    public async Task<int> GetWorkerStatusSummaryOverHttp()
    {
        var response = await this.host.Client.GetAsync("/workable/workers/status-summary");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected status-summary JSON response.");
        return json["total"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Expected total worker count.");
    }

    [Benchmark]
    public async Task<int> GetFilteredWorkerStatusSummaryOverHttp()
    {
        var response = await this.host.Client.PostAsJsonAsync(
            "/workable/workers/status-summary",
            new
            {
                definitionName = "perf.transport.queued",
                take = SeedWorkerCount,
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected filtered status-summary JSON response.");
        return json["total"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Expected filtered total worker count.");
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.host.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

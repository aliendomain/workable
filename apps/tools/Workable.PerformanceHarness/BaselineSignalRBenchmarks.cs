using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.SignalR.Client;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Experimental SignalR connection benchmarks. Deterministic realtime delivery measurement lives in the scenario runner.
/// </summary>
public class ExperimentalSignalRConnectionBenchmarks
{
    private TransportBenchmarkHost host = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        this.host = TransportBenchmarkHost.Create().GetAwaiter().GetResult();
        this.host.Gates.Reset();
    }

    [Benchmark(Baseline = true)]
    public async Task<bool> ConnectAndWatchEvents()
    {
        await using var connection = this.host.CreateSignalRConnection();
        await connection.StartAsync();
        await connection.InvokeAsync("WatchEvents", null, null);
        return true;
    }

    [Benchmark]
    public async Task<bool> ConnectWatchAndUnwatchEvents()
    {
        await using var connection = this.host.CreateSignalRConnection();
        await connection.StartAsync();
        await connection.InvokeAsync("WatchEvents", null, null);
        await connection.InvokeAsync("UnwatchEvents", null, null);
        return true;
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.host.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

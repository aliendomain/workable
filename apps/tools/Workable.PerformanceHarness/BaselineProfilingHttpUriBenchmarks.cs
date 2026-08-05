using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks URI sanitization before and after retained HTTP profile text is bounded.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class BaselineProfilingHttpUriBenchmarks
{
    private string uri = null!;

    [Params(128, 32_768, 1_000_000)]
    public int PathLength { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.uri = $"https://user:password@example.test:8443/{new string('x', this.PathLength)}?token=secret#fragment";

    [Benchmark]
    public string? CaptureSanitizedUri()
        => WorkableHttpClientProfilingObserver.CaptureUriForBenchmark(this.uri);
}

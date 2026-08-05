using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks profile snapshot materialization for large flat and deeply nested trees.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineProfilingSnapshotBenchmarks
{
    private const int FlatNodeCount = 10_000;
    private const int NestedScopeDepth = 5_000;
    private const int RenderedScopeDepth = 1_000;
    private WorkProfile flatProfile = null!;
    private WorkProfile nestedProfile = null!;
    private WorkProfileSnapshot renderedSnapshot = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.flatProfile = new WorkProfile(
            "flat",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);
        for (var index = 0; index < FlatNodeCount; index++)
        {
            this.flatProfile.TryAddAutomaticInfo("http.client", "HTTP Request");
        }

        this.nestedProfile = new WorkProfile(
            "nested",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);
        var scopes = new IWorkProfileScope[NestedScopeDepth];
        for (var index = 0; index < scopes.Length; index++)
        {
            scopes[index] = this.nestedProfile.CreateScope("nested scope");
        }

        for (var index = scopes.Length - 1; index >= 0; index--)
        {
            scopes[index].Dispose();
        }

        var renderedProfile = new WorkProfile(
            "rendered",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);
        var renderedScopes = new IWorkProfileScope[RenderedScopeDepth];
        for (var index = 0; index < renderedScopes.Length; index++)
        {
            renderedScopes[index] = renderedProfile.CreateScope("nested scope");
        }

        for (var index = renderedScopes.Length - 1; index >= 0; index--)
        {
            renderedScopes[index].Dispose();
        }

        this.renderedSnapshot = renderedProfile.ToSnapshot();
    }

    [Benchmark(Baseline = true)]
    public WorkProfileSnapshot SnapshotFlatProfile()
        => this.flatProfile.ToSnapshot();

    [Benchmark]
    public WorkProfileSnapshot SnapshotDeepProfile()
        => this.nestedProfile.ToSnapshot();

    [Benchmark]
    public string RenderDeepProfile()
        => this.renderedSnapshot.ToAsciiTree();
}

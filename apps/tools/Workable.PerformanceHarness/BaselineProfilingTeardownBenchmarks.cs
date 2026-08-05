using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks unregistering one system while the shared HTTP observer tracks work for several systems.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineProfilingTeardownBenchmarks
{
    private const string ActivitySourceName = "System.Net.Http";
    private const string RequestActivityName = "System.Net.Http.HttpRequestOut";
    private const int SystemCount = 8;
    private const int ActiveRequestsPerSystem = 128;

    private readonly IWorkProfilingContextAccessor accessor = new WorkProfilingContextAccessor();
    private readonly List<Activity> activities = [];
    private readonly WorkSystemId[] systemIds = Enumerable.Range(0, SystemCount)
        .Select(_ => WorkSystemId.New())
        .ToArray();
    private ActivitySource source = null!;
    private WorkableHttpClientProfilingObserver observer = null!;

    [GlobalSetup]
    public void GlobalSetup()
        => this.source = new ActivitySource(ActivitySourceName);

    [IterationSetup]
    public void IterationSetup()
    {
        this.observer = new WorkableHttpClientProfilingObserver(this.accessor);
        foreach (var systemId in this.systemIds)
        {
            this.observer.RegisterSystem(systemId);
            var profile = new WorkProfile(
                "benchmark",
                maximumAutomaticInstrumentationNodes: 1,
                WorkProfileCaptureMode.Full);
            using var ambient = WorkProfilerContext.Begin(systemId, profile);
            for (var index = 0; index < ActiveRequestsPerSystem; index++)
            {
                var activity = this.source.StartActivity(
                    RequestActivityName,
                    ActivityKind.Client,
                    default(ActivityContext),
                    [
                        new KeyValuePair<string, object?>("http.request.method", "GET"),
                        new KeyValuePair<string, object?>(
                            "url.full",
                            $"https://example.test/{systemId}/{index}"),
                    ]) ?? throw new InvalidOperationException("The benchmark HTTP activity was not sampled.");
                this.activities.Add(activity);
                Activity.Current = null;
            }
        }
    }

    [Benchmark]
    public void UnregisterOneSystem()
        => this.observer.UnregisterSystem(this.systemIds[0]);

    [IterationCleanup]
    public void IterationCleanup()
    {
        foreach (var activity in this.activities)
        {
            activity.Stop();
            activity.Dispose();
        }

        this.activities.Clear();
        this.observer.Dispose();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.source.Dispose();
}

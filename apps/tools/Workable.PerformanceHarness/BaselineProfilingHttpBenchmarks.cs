using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks HTTP activity sampling after a bounded profile reaches capacity and during a concurrent burst.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineProfilingHttpBenchmarks
{
    private const string ActivitySourceName = "System.Net.Http";
    private const string ControlActivitySourceName = "Workable.Performance.HttpControl";
    private const string RequestActivityName = "System.Net.Http.HttpRequestOut";
    private const int OperationsPerInvocation = 1_000;
    private const int ConcurrentRequests = 32;

    private readonly WorkSystemId systemId = WorkSystemId.New();
    private readonly IWorkProfilingContextAccessor accessor = new WorkProfilingContextAccessor();
    private ActivitySource source = null!;
    private ActivitySource controlSource = null!;
    private ActivityListener forcingListener = null!;
    private WorkableHttpClientProfilingObserver observer = null!;
    private WorkProfile postCapProfile = null!;
    private WorkProfile admittedProfile = null!;
    private KeyValuePair<string, object?>[] requestTags = null!;
    private KeyValuePair<string, object?>[] oversizedRequestTags = null!;
    private SamplingBarrier? samplingBarrier;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.source = new ActivitySource(ActivitySourceName);
        this.controlSource = new ActivitySource(ControlActivitySourceName);
        this.forcingListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal) ||
                string.Equals(source.Name, ControlActivitySourceName, StringComparison.Ordinal),
            Sample = this.Sample,
            SampleUsingParentId = this.Sample,
        };
        ActivitySource.AddActivityListener(this.forcingListener);
        this.observer = new WorkableHttpClientProfilingObserver(this.systemId, this.accessor);
        this.requestTags =
        [
            new("http.request.method", "GET"),
            new("url.full", "https://example.test/orders/42?include=items"),
        ];
        this.oversizedRequestTags =
        [
            new("http.request.method", "GET"),
            new("url.full", $"https://example.test/{new string('x', 1_000_000)}?include=items"),
        ];
    }

    [IterationSetup]
    public void IterationSetup()
    {
        this.postCapProfile = new WorkProfile("benchmark", maximumAutomaticInstrumentationNodes: 1);
        this.postCapProfile.TryAddAutomaticInfo("benchmark.setup", "admitted");
        this.admittedProfile = new WorkProfile(
            "benchmark",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvocation)]
    public int PostCapRequestsWithIndependentTracing()
    {
        using var ambient = WorkProfilerContext.Begin(this.systemId, this.postCapProfile);
        var created = 0;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            using var activity = this.source.StartActivity(RequestActivityName, ActivityKind.Client);
            created += activity is null ? 0 : 1;
        }

        return created;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int RequestsOutsideWorkableContext()
        => this.StartRequests(this.source, addResponseTags: false);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int ControlRequestsWithoutWorkableListener()
        => this.StartRequests(this.controlSource, addResponseTags: false);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int AdmittedRequests()
    {
        using var ambient = WorkProfilerContext.Begin(this.systemId, this.admittedProfile);
        return this.StartRequests(this.source, addResponseTags: true);
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int AdmittedRequestsWithOversizedUri()
    {
        using var ambient = WorkProfilerContext.Begin(this.systemId, this.admittedProfile);
        return this.StartRequests(
            this.source,
            addResponseTags: true,
            this.oversizedRequestTags);
    }

    [Benchmark]
    public int ConcurrentRequestsAtProfileCap()
    {
        var profile = new WorkProfile("benchmark", maximumAutomaticInstrumentationNodes: 1);
        using var ambient = WorkProfilerContext.Begin(this.systemId, profile);
        using var barrier = new SamplingBarrier(ConcurrentRequests);
        Volatile.Write(ref this.samplingBarrier, barrier);
        try
        {
            var starts = Enumerable.Range(0, ConcurrentRequests)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        using var activity = this.source.StartActivity(RequestActivityName, ActivityKind.Client);
                        return activity?.IsAllDataRequested == true ? 1 : 0;
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
            barrier.WaitUntilArrived();
            barrier.Release();
            return Task.WhenAll(starts).GetAwaiter().GetResult().Sum();
        }
        finally
        {
            Volatile.Write(ref this.samplingBarrier, null);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.observer.Dispose();
        this.forcingListener.Dispose();
        this.source.Dispose();
        this.controlSource.Dispose();
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
        => this.Sample();

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<string> options)
        => this.Sample();

    private ActivitySamplingResult Sample()
    {
        var barrier = Volatile.Read(ref this.samplingBarrier);
        if (barrier is null)
        {
            return ActivitySamplingResult.AllData;
        }

        barrier.ArriveAndWaitForRelease();
        return ActivitySamplingResult.None;
    }

    private int StartRequests(
        ActivitySource activitySource,
        bool addResponseTags,
        KeyValuePair<string, object?>[]? tags = null)
    {
        tags ??= this.requestTags;
        var created = 0;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            using var activity = activitySource.StartActivity(
                RequestActivityName,
                ActivityKind.Client,
                default(ActivityContext),
                tags);
            if (activity is null)
            {
                continue;
            }

            if (addResponseTags)
            {
                activity.SetTag("http.response.status_code", 200);
            }

            created++;
        }

        return created;
    }

    private sealed class SamplingBarrier(int participants) : IDisposable
    {
        private readonly CountdownEvent arrived = new(participants);
        private readonly ManualResetEventSlim released = new();

        public void ArriveAndWaitForRelease()
        {
            this.arrived.Signal();
            this.released.Wait();
        }

        public void WaitUntilArrived()
        {
            if (!this.arrived.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("HTTP benchmark requests did not reach the sampling barrier.");
            }
        }

        public void Release() => this.released.Set();

        public void Dispose()
        {
            this.released.Set();
            this.released.Dispose();
            this.arrived.Dispose();
        }
    }
}

using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks profile publication while settled asynchronous instrumentation remains active.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineProfilingFinalizationBenchmarks
{
    private WorkProfile profile = null!;

    [Params(0, 100, 1_000)]
    public int PendingOperations { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        this.profile = new WorkProfile(
            "benchmark",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);
        var registry = (IWorkProfilePendingInstrumentationRegistry)this.profile;
        for (var index = 0; index < this.PendingOperations; index++)
        {
            if (!registry.TryEnterPendingInstrumentationRegistration())
            {
                throw new InvalidOperationException("The benchmark profile stopped accepting instrumentation.");
            }

            try
            {
                registry.RegisterPendingInstrumentation(new PendingInstrumentation());
            }
            finally
            {
                registry.ExitPendingInstrumentationRegistration();
            }
        }
    }

    [Benchmark]
    public WorkProfileSnapshot FinalizeProfile()
        => this.profile.ToSnapshot();

    private sealed class PendingInstrumentation : IWorkProfilePendingInstrumentation
    {
        public void FinalizeForProfileSnapshot()
        {
        }
    }
}

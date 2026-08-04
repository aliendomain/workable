using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks bounded profile admission and temporary full-capture rule lookup hot paths.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineProfilingAdmissionBenchmarks
{
    private const int OperationsPerInvocation = 1_000;
    private readonly WorkActor actor = new("perf-profile-actor");
    private WorkRequestContext requestContext = null!;
    private WorkProfileCaptureRuleStore emptyRules = null!;
    private WorkProfileCaptureRuleStore populatedRules = null!;
    private WorkProfileCaptureRuleStore exhaustedMatchingRules = null!;
    private WorkProfileCaptureRuleStore oneShotRules = null!;
    private WorkProfileCaptureRuleStore.WorkProfileCaptureRuleLease[] pendingRuleLeases = null!;
    private string[] uniqueInstrumentationKeys = null!;
    private WorkProfile stableOmissionProfile = null!;
    private WorkProfile uniqueOmissionProfile = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess, this.actor);
        this.emptyRules = new WorkProfileCaptureRuleStore();
        this.populatedRules = new WorkProfileCaptureRuleStore();
        this.exhaustedMatchingRules = new WorkProfileCaptureRuleStore();
        this.pendingRuleLeases = new WorkProfileCaptureRuleStore.WorkProfileCaptureRuleLease[
            WorkProfileCaptureRuleStore.MaximumActiveRules];
        for (var index = 0; index < WorkProfileCaptureRuleStore.MaximumActiveRules; index++)
        {
            this.populatedRules.Create(
                $"perf.unrelated.{index}",
                actorId: null,
                maximumMatches: 1,
                TimeSpan.FromHours(1),
                this.actor);
            this.exhaustedMatchingRules.Create(
                "perf.target",
                actorId: null,
                maximumMatches: 1,
                TimeSpan.FromHours(1),
                this.actor);
            this.pendingRuleLeases[index] = this.exhaustedMatchingRules.TryAcquire(
                "perf.target",
                this.requestContext)!;
        }

        this.uniqueInstrumentationKeys = Enumerable.Range(0, OperationsPerInvocation)
            .Select(static index => $"custom.client.{index}")
            .ToArray();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        this.stableOmissionProfile = CreateCappedProfile();
        this.uniqueOmissionProfile = CreateCappedProfile();
        this.oneShotRules = new WorkProfileCaptureRuleStore();
        for (var index = 0; index < WorkProfileCaptureRuleStore.MaximumActiveRules; index++)
        {
            this.oneShotRules.Create(
                "perf.consume",
                actorId: null,
                maximumMatches: 1,
                TimeSpan.FromHours(1),
                this.actor);
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvocation)]
    public int CaptureRuleMissWithoutRules()
        => this.MeasureRuleMisses(this.emptyRules);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int CaptureRuleMissWithMaximumRules()
        => this.MeasureRuleMisses(this.populatedRules);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int CaptureRuleMissWithMaximumExhaustedMatchingRules()
        => this.MeasureRuleMisses(this.exhaustedMatchingRules);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int RecordOmissionsForStableInstrumentationKey()
    {
        var omitted = 0;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            omitted += this.stableOmissionProfile.TryAddAutomaticInfo(
                "http.client",
                "omitted") ? 0 : 1;
        }

        return omitted;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int RecordOmissionsForUniqueInstrumentationKeys()
    {
        var omitted = 0;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            omitted += this.uniqueOmissionProfile.TryAddAutomaticInfo(
                this.uniqueInstrumentationKeys[index],
                "omitted") ? 0 : 1;
        }

        return omitted;
    }

    [Benchmark(OperationsPerInvoke = WorkProfileCaptureRuleStore.MaximumActiveRules)]
    public int ConsumeMaximumOneShotCaptureRules()
    {
        var committed = 0;
        for (var index = 0; index < WorkProfileCaptureRuleStore.MaximumActiveRules; index++)
        {
            using var lease = this.oneShotRules.TryAcquire("perf.consume", this.requestContext);
            if (lease is null)
            {
                continue;
            }

            lease.Commit();
            committed++;
        }

        return committed;
    }

    private int MeasureRuleMisses(WorkProfileCaptureRuleStore rules)
    {
        var matches = 0;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            using var lease = rules.TryAcquire("perf.target", this.requestContext);
            matches += lease is null ? 0 : 1;
        }

        return matches;
    }

    private static WorkProfile CreateCappedProfile()
    {
        var profile = new WorkProfile("benchmark", maximumAutomaticInstrumentationNodes: 1);
        profile.TryAddAutomaticInfo("benchmark.setup", "admitted");
        return profile;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var lease in this.pendingRuleLeases)
        {
            lease.Dispose();
        }
    }
}

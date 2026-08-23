using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkProfilingContextAccessorShould
{
    [Fact]
    public void ResolveTheCurrentSystemOwnedProfilingContext()
    {
        var accessor = new WorkProfilingContextAccessor();
        var profile = new WorkProfile("root");
        var systemId = WorkSystemId.New();

        Assert.False(accessor.TryGetCurrent(out _));

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            Assert.True(accessor.TryGetCurrent(out var context));
            Assert.Equal(systemId, context.SystemId);
            Assert.Same(profile, context.Profiler);
        }

        Assert.False(accessor.TryGetCurrent(out _));
    }

    [Fact]
    public void AutomaticContributionMethodsRemainCompatibleWithPlainProfilers()
    {
        var context = new WorkProfilingContext(WorkSystemId.New(), NoOpWorkProfiler.Instance);

        Assert.True(context.TryAddAutomaticInfo("custom.client", "Custom info"));
        Assert.True(context.TryStartAutomaticTiming(
            "custom.client",
            "Custom timing",
            context: null,
            out var scope));

        Assert.NotNull(scope);
        scope.Dispose();
    }

    [Fact]
    public void AutomaticContributionMethodsUseAdmissionAwareProfilersAndLazyFactories()
    {
        var profile = new WorkProfile("root");
        var context = new WorkProfilingContext(WorkSystemId.New(), profile);
        var factoryCalls = 0;

        Assert.True(context.TryAddAutomaticInfo("test", "info", new { Value = 1 }));
        Assert.True(context.TryAddAutomaticInfo(
            "test",
            "lazy-info",
            () =>
            {
                factoryCalls++;
                return new { Value = 2 };
            }));
        Assert.True(context.TryStartAutomaticTiming(
            "test",
            "timing",
            context: null,
            out var timing));
        Assert.True(context.TryStartAutomaticTiming(
            "test",
            "lazy-timing",
            () =>
            {
                factoryCalls++;
                return new object();
            },
            out var timingContext,
            out var lazyTiming));

        Assert.Equal(2, factoryCalls);
        Assert.NotNull(timingContext);
        Assert.NotNull(timing);
        Assert.NotNull(lazyTiming);
        timing.Dispose();
        lazyTiming.Dispose();
        Assert.Throws<ArgumentNullException>(() =>
            context.TryAddAutomaticInfo<object>("test", "invalid", null!));
        Assert.Throws<ArgumentNullException>(() =>
            context.TryStartAutomaticTiming<object>(
                "test",
                "invalid",
                null!,
                out _,
                out _));
    }

    [Fact]
    public void LazyAutomaticContributionMethodsRemainCompatibleWithPlainProfilers()
    {
        var context = new WorkProfilingContext(WorkSystemId.New(), NoOpWorkProfiler.Instance);

        Assert.True(context.TryAddAutomaticInfo("custom.client", "Custom info", () => "context"));
        Assert.True(context.TryStartAutomaticTiming(
            "custom.client",
            "Custom timing",
            () => new object(),
            out var timingContext,
            out var scope));

        Assert.NotNull(timingContext);
        Assert.NotNull(scope);
        scope.Dispose();
    }
}

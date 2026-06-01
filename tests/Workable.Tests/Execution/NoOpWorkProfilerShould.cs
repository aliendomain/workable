using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class NoOpWorkProfilerShould
{
    [Fact]
    public void AcceptAllProfilerOperationsWithoutRecordingState()
    {
        var profiler = NoOpWorkProfiler.Instance;

        var exception = Record.Exception(() =>
        {
            profiler.AddInfo("info", new { Value = 1 });
            using var timing = profiler.StartTiming("timing");
            timing.SetResult(new { Result = true });
            using var scope = profiler.CreateScope("scope");
            scope.SetResult("done");
            using var methodScope = profiler.CreateMethodScope(
                typeof(NoOpWorkProfilerShould),
                nameof(AcceptAllProfilerOperationsWithoutRecordingState),
                context: new { Input = true });
            using var genericMethodScope = profiler.CreateMethodScope<NoOpWorkProfilerShould>(
                context: new { Input = "generic" });
        });

        Assert.Null(exception);
    }

    [Fact]
    public void ReuseNoOpScopesSafely()
    {
        var profiler = NoOpWorkProfiler.Instance;

        var first = profiler.StartTiming("first");
        var second = profiler.CreateScope("second");

        Assert.Same(first, second);
        first.Dispose();
        second.SetResult("after dispose");
        second.Dispose();
    }
}

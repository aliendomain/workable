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
}

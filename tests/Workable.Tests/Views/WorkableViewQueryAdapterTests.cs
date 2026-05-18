using Workable;

namespace Workable.Tests;

[Trait("Category", "Views")]
public sealed class WorkableViewQueryAdapterTests
{
    [Fact]
    public void RequiresIntervalPublishReturnsTrueForThroughputComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        var requiresIntervalPublish = adapter.RequiresIntervalPublish(
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("throughput", "throughput"),
            ]));

        Assert.True(requiresIntervalPublish);
    }

    [Fact]
    public void RequiresIntervalPublishReturnsFalseForStateBasedOverviewComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        var requiresIntervalPublish = adapter.RequiresIntervalPublish(
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("workers", "workers"),
            ]));

        Assert.False(requiresIntervalPublish);
    }
}

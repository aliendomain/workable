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

    [Fact]
    public void NormalizeViewCriteriaReturnsDefaultWorkerComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        var criteria = adapter.NormalizeViewCriteria("worker");
        var components = Assert.IsAssignableFrom<IReadOnlyList<WorkComponentRequest>>(criteria.Components);

        Assert.Equal(["worker", "currentIteration"], components.Select(component => component.Id).ToArray());
        Assert.Equal(["workerDetail", "workerCurrentIteration"], components.Select(component => component.Type).ToArray());
        Assert.All(components, component => Assert.Equal(WorkComponentShapes.Detailed, component.Shape));
    }
}

using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkProfileShould
{
    [Fact]
    public void BuildSnapshotTreeWithScopesTimingsMethodInputsAndResults()
    {
        var profile = new WorkProfile("root");
        var input = new { OrderId = "order-123" };

        profile.AddInfo("root info");
        using (var outer = profile.CreateScope("outer scope", context: "batch"))
        {
            using (profile.StartTiming("timed operation"))
            {
            }

            using var method = profile.CreateMethodScope(
                typeof(WorkProfileShould),
                nameof(BuildSnapshotTreeWithScopesTimingsMethodInputsAndResults),
                input,
                label: "Request");
            method.SetResult("accepted");
            outer.SetResult("done");
        }

        var snapshot = profile.ToSnapshot();
        var root = snapshot.Root;
        var outerScope = Assert.Single(root.Children, child => child.Label == "outer scope (batch)");
        var methodScope = Assert.Single(outerScope.Children, child =>
            child.MetricType == WorkProfileMetricType.MethodScope &&
            child.Label.Contains(nameof(BuildSnapshotTreeWithScopesTimingsMethodInputsAndResults), StringComparison.Ordinal));

        Assert.Equal(WorkProfileMetricType.Scope, root.MetricType);
        Assert.Equal("root", root.Label);
        Assert.Contains(root.Children, child => child is { MetricType: WorkProfileMetricType.Metric, Label: "root info" });
        Assert.Equal(WorkProfileMetricType.Scope, outerScope.MetricType);
        Assert.Equal("batch", outerScope.Context);
        Assert.Contains(outerScope.Children, child => child is { MetricType: WorkProfileMetricType.Timing, Label: "timed operation" });
        Assert.Contains(outerScope.Children, child => child.Label == "Result (done)");
        Assert.Contains(methodScope.Children, child =>
            child is { MetricType: WorkProfileMetricType.Metric, Label: "Request" } &&
            ReferenceEquals(input, child.Context));
        Assert.Contains(methodScope.Children, child => child.Label == "Result (accepted)");
        Assert.True(snapshot.StartedAt <= snapshot.CapturedAt);
    }

    [Fact]
    public void RejectScopesDisposedOutOfCreationOrder()
    {
        var profile = new WorkProfile("root");
        using var outer = profile.CreateScope("outer");
        using var inner = profile.CreateScope("inner");

        var exception = Assert.Throws<InvalidOperationException>(() => outer.Dispose());

        Assert.Equal("Profile scopes must be disposed in reverse order of creation.", exception.Message);
        inner.Dispose();
    }
}

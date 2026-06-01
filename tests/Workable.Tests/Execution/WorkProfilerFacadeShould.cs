using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkProfilerFacadeShould
{
    [Fact]
    public void DelegateOperationsToTheCurrentProfilerContext()
    {
        var facade = new WorkProfilerFacade();
        var profile = new WorkProfile("root");

        using (WorkProfilerContext.Begin(profile))
        {
            facade.AddInfo("facade info");
            using (var scope = facade.CreateScope("facade scope"))
            {
                facade.AddInfo("nested info");
                scope.SetResult("scope result");
            }

            using var timing = facade.StartTiming("facade timing");
            using var method = facade.CreateMethodScope(
                typeof(WorkProfilerFacadeShould),
                nameof(DelegateOperationsToTheCurrentProfilerContext),
                context: "method input");
        }

        var labels = Flatten(profile.ToSnapshot().Root).Select(node => node.Label).ToArray();
        Assert.Contains("facade info", labels);
        Assert.Contains("facade scope", labels);
        Assert.Contains("nested info", labels);
        Assert.Contains("Result (scope result)", labels);
        Assert.Contains("facade timing", labels);
        Assert.Contains(labels, label => label.Contains(
            $"{nameof(WorkProfilerFacadeShould)}.{nameof(DelegateOperationsToTheCurrentProfilerContext)}",
            StringComparison.Ordinal));
    }

    [Fact]
    public void RestorePreviousProfilerContextWhenNestedContextsAreDisposed()
    {
        var facade = new WorkProfilerFacade();
        var outer = new WorkProfile("outer");
        var inner = new WorkProfile("inner");

        using (WorkProfilerContext.Begin(outer))
        {
            facade.AddInfo("outer before");
            using (WorkProfilerContext.Begin(inner))
            {
                facade.AddInfo("inner only");
            }

            facade.AddInfo("outer after");
        }

        facade.AddInfo("outside context");

        var outerLabels = Flatten(outer.ToSnapshot().Root).Select(node => node.Label).ToArray();
        var innerLabels = Flatten(inner.ToSnapshot().Root).Select(node => node.Label).ToArray();
        Assert.Contains("outer before", outerLabels);
        Assert.Contains("outer after", outerLabels);
        Assert.DoesNotContain("inner only", outerLabels);
        Assert.DoesNotContain("outside context", outerLabels);
        Assert.Contains("inner only", innerLabels);
        Assert.DoesNotContain("outer before", innerLabels);
        Assert.DoesNotContain("outer after", innerLabels);
    }

    private static IEnumerable<WorkProfileSnapshotNode> Flatten(WorkProfileSnapshotNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}

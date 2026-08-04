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

            using var method = profile.CreateMethodScope<WorkProfileShould>(
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

    [Fact]
    public void BoundAutomaticInstrumentationAcrossSourcesAndReportOmissions()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 2);

        Assert.True(profile.TryStartAutomaticTiming("http.client", "HTTP Request", null, out var http));
        http!.Dispose();
        Assert.True(profile.TryAddAutomaticInfo("sql.client", "SQL Error"));
        Assert.False(profile.TryStartAutomaticTiming("sql.client", "SQL Command", null, out var omitted));
        Assert.False(profile.TryAddAutomaticInfo("http.client", "HTTP Request"));
        Assert.False(profile.TryAddAutomaticInfo("other", "Reserved omission bucket"));
        profile.AddInfo("Explicit application node");

        var snapshot = profile.ToSnapshot();
        var summary = Assert.Single(
            snapshot.Root.Children,
            node => node.Label == "Automatic instrumentation truncated");
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary.Context);

        Assert.Null(omitted);
        Assert.Contains(snapshot.Root.Children, node => node.Label == "HTTP Request");
        Assert.Contains(snapshot.Root.Children, node => node.Label == "SQL Error");
        Assert.Contains(snapshot.Root.Children, node => node.Label == "Explicit application node");
        Assert.Contains("http.client", summaryJson, StringComparison.Ordinal);
        Assert.Contains("sql.client", summaryJson, StringComparison.Ordinal);
        Assert.Contains("\"other\":1", summaryJson, StringComparison.Ordinal);
        Assert.Contains("\"MaximumNodes\":2", summaryJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FullCaptureBypassesAutomaticInstrumentationLimit()
    {
        var profile = new WorkProfile(
            "root",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);

        for (var index = 0; index < 10; index++)
        {
            Assert.True(profile.TryAddAutomaticInfo("http.client", $"HTTP {index}"));
        }

        var snapshot = profile.ToSnapshot();

        Assert.Equal(10, snapshot.Root.Children.Count);
        Assert.DoesNotContain(
            snapshot.Root.Children,
            node => node.Label == "Automatic instrumentation truncated");
    }

    [Fact]
    public void SnapshotLargeFullProfileWithoutLosingNodes()
    {
        const int nodeCount = 10_000;
        var profile = new WorkProfile(
            "root",
            maximumAutomaticInstrumentationNodes: 1,
            WorkProfileCaptureMode.Full);

        for (var index = 0; index < nodeCount; index++)
        {
            Assert.True(profile.TryAddAutomaticInfo("http.client", $"HTTP {index}"));
        }

        var snapshot = profile.ToSnapshot();

        Assert.Equal(nodeCount, snapshot.Root.Children.Count);
        Assert.Equal("HTTP 0", snapshot.Root.Children[0].Label);
        Assert.Equal($"HTTP {nodeCount - 1}", snapshot.Root.Children[^1].Label);
    }

    [Fact]
    public void CreateAutomaticContextOnlyAfterAdmissionSucceeds()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        var createdContexts = 0;

        Assert.True(profile.TryAddAutomaticInfo(
            "sql.client",
            "First",
            () =>
            {
                createdContexts++;
                return new { Value = 1 };
            }));
        Assert.False(profile.TryAddAutomaticInfo<object>(
            "sql.client",
            "Omitted info",
            () =>
            {
                createdContexts++;
                return new object();
            }));
        Assert.False(profile.TryStartAutomaticTiming<object>(
            "sql.client",
            "Omitted",
            () =>
            {
                createdContexts++;
                return new object();
            },
            out object? omittedContext,
            out var omittedScope));

        Assert.Equal(1, createdContexts);
        Assert.Null(omittedContext);
        Assert.Null(omittedScope);
    }

    [Fact]
    public void CreateLazyContextForAnAdmittedAutomaticTiming()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        var expectedContext = new object();

        Assert.True(profile.TryStartAutomaticTiming(
            "http.client",
            "HTTP Request",
            () => expectedContext,
            out object? context,
            out var scope));
        scope!.Dispose();

        Assert.Same(expectedContext, context);
        Assert.Contains(profile.ToSnapshot().Root.Children, node => node.Label == "HTTP Request");
    }

    [Fact]
    public void ReleaseAutomaticAdmissionWhenLazyContextCreationFails()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);

        Assert.Throws<InvalidOperationException>(() => profile.TryAddAutomaticInfo<object>(
            "sql.client",
            "Broken",
            () => throw new InvalidOperationException("context failed")));

        Assert.True(profile.TryAddAutomaticInfo("http.client", "Recovered"));
        Assert.Contains(profile.ToSnapshot().Root.Children, node => node.Label == "Recovered");
    }

    [Fact]
    public void ReleaseAutomaticTimingAdmissionWhenLazyContextCreationFails()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);

        Assert.Throws<InvalidOperationException>(() => profile.TryStartAutomaticTiming<object>(
            "http.client",
            "Broken",
            () => throw new InvalidOperationException("context failed"),
            out _,
            out _));

        Assert.True(profile.TryAddAutomaticInfo("sql.client", "Recovered"));
        Assert.Contains(profile.ToSnapshot().Root.Children, node => node.Label == "Recovered");
    }

    [Fact]
    public void ReleaseSampledTimingAdmissionWhenItsLazyContextCreationFails()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        var samplingGate = Assert.IsAssignableFrom<IWorkAutomaticProfileSamplingGate>(profile);
        Assert.True(samplingGate.TryReserveAutomaticNodeForSampling("http.client"));

        Assert.Throws<InvalidOperationException>(() => samplingGate.TryStartReservedAutomaticTiming<object>(
            "HTTP Request",
            () => throw new InvalidOperationException("context failed"),
            out _,
            out _));

        Assert.True(profile.TryAddAutomaticInfo("sql.client", "Recovered"));
        Assert.Contains(profile.ToSnapshot().Root.Children, node => node.Label == "Recovered");
    }

    [Fact]
    public void ReleaseAnUnusedSamplingReservationBackToTheSharedBudget()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        var samplingGate = Assert.IsAssignableFrom<IWorkAutomaticProfileSamplingGate>(profile);

        Assert.True(samplingGate.TryReserveAutomaticNodeForSampling("http.client"));
        samplingGate.ReleaseReservedAutomaticNode();

        Assert.True(profile.TryAddAutomaticInfo("sql.client", "Recovered"));
    }

    [Fact]
    public void ExposeStartTimeAndAllowIdempotentTimingCompletion()
    {
        var profile = new WorkProfile("root");
        var timing = profile.StartTiming("timed");

        timing.SetResult("ignored for a plain timing");
        timing.Dispose();
        timing.Dispose();
        var snapshot = profile.ToSnapshot();

        Assert.Equal(profile.StartedAt, snapshot.StartedAt);
        Assert.Single(snapshot.Root.Children, node => node.Label == "timed");
    }

    [Fact]
    public void EnforceAutomaticInstrumentationLimitUnderConcurrency()
    {
        const int maximum = 50;
        var profile = new WorkProfile("root", maximum);

        Parallel.For(0, 1_000, index =>
            profile.TryAddAutomaticInfo(
                index % 2 == 0 ? "http.client" : "sql.client",
                $"Automatic {index}"));

        var snapshot = profile.ToSnapshot();
        var automaticNodes = snapshot.Root.Children
            .Where(node => node.Label.StartsWith("Automatic ", StringComparison.Ordinal) &&
                node.Label != "Automatic instrumentation truncated")
            .ToList();
        var summary = Assert.Single(
            snapshot.Root.Children,
            node => node.Label == "Automatic instrumentation truncated");
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary.Context);
        using var summaryDocument = System.Text.Json.JsonDocument.Parse(summaryJson);
        var omittedCount = summaryDocument.RootElement
            .GetProperty("OmittedByInstrumentation")
            .EnumerateObject()
            .Sum(entry => entry.Value.GetInt32());

        Assert.Equal(maximum, automaticNodes.Count);
        Assert.Equal(950, omittedCount);
    }

    [Fact]
    public void BoundOmissionInstrumentationCardinalityAndKeyLength()
    {
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        Assert.True(profile.TryAddAutomaticInfo("setup", "Captured"));

        for (var index = 0; index < 1_000; index++)
        {
            Assert.False(profile.TryAddAutomaticInfo(
                $"{index:D4}.{new string('x', 200)}",
                "Omitted"));
        }

        var summary = Assert.Single(
            profile.ToSnapshot().Root.Children,
            node => node.Label == "Automatic instrumentation truncated");
        using var document = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(summary.Context));
        var omissions = document.RootElement.GetProperty("OmittedByInstrumentation");
        var entries = omissions.EnumerateObject().ToArray();

        Assert.True(entries.Length <= 33);
        Assert.All(entries, entry => Assert.True(entry.Name.Length <= 128));
        Assert.Equal(1_000, entries.Sum(entry => entry.Value.GetInt32()));
        Assert.True(omissions.GetProperty("other").GetInt32() > 0);
    }

    [Fact]
    public void SnapshotVeryDeepProfileWithoutUsingTheCallStack()
    {
        const int depth = 30_000;
        var profile = new WorkProfile("root");
        var scopes = new IWorkProfileScope[depth];
        for (var index = 0; index < scopes.Length; index++)
        {
            scopes[index] = profile.CreateScope($"scope {index}");
        }

        for (var index = scopes.Length - 1; index >= 0; index--)
        {
            scopes[index].Dispose();
        }

        var node = profile.ToSnapshot().Root;
        for (var index = 0; index < depth; index++)
        {
            node = Assert.Single(node.Children);
            Assert.Equal($"scope {index}", node.Label);
        }

        Assert.Empty(node.Children);

        var rendered = profile.ToSnapshot().ToAsciiTree();
        Assert.Contains("scope 0", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("scope 1000", rendered, StringComparison.Ordinal);
        Assert.Contains("profile rendering truncated", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundAsciiRenderingByNodeCountAndDepth()
    {
        var profile = new WorkProfile("root");
        for (var index = 0; index < 100; index++)
        {
            profile.AddInfo($"entry {index}");
        }

        var snapshot = profile.ToSnapshot();
        var rendered = snapshot.ToAsciiTree(maximumNodes: 10, maximumDepth: 5);

        Assert.Contains("entry 8", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("entry 9", rendered, StringComparison.Ordinal);
        Assert.Contains("profile rendering truncated", rendered, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ToAsciiTree(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ToAsciiTree(1, 0));
    }

    [Fact]
    public async Task WaitForAnEnteredInstrumentationRegistrationWithoutPublishingEarly()
    {
        var profile = new WorkProfile("root");
        var registry = (IWorkProfilePendingInstrumentationRegistry)profile;
        Assert.True(registry.TryEnterPendingInstrumentationRegistration());

        var snapshotTask = Task.Run(profile.ToSnapshot);
        Assert.True(SpinWait.SpinUntil(
            () => !registry.IsAcceptingPendingInstrumentation,
            TimeSpan.FromSeconds(5)));
        Assert.False(snapshotTask.IsCompleted);

        registry.ExitPendingInstrumentationRegistration();
        var snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("root", snapshot.Root.Label);
    }

    [Fact]
    public void RejectInvalidAutomaticInstrumentationLimit()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new WorkProfile("root", 0));

        Assert.Equal("maximumAutomaticInstrumentationNodes", exception.ParamName);
    }

    [Fact]
    public void RejectAutomaticNodesAfterSnapshotFinalization()
    {
        var profile = new WorkProfile("root");

        profile.ToSnapshot();

        Assert.False(profile.TryAddAutomaticInfo("http.client", "Too late"));
        Assert.False(profile.TryStartAutomaticTiming("sql.client", "Too late", null, out var scope));
        Assert.False(profile.TryAddAutomaticInfo<object>(
            "http.client",
            "Too late",
            () => throw new InvalidOperationException("Factory must not run.")));
        Assert.False(profile.TryStartAutomaticTiming<object>(
            "sql.client",
            "Too late",
            () => throw new InvalidOperationException("Factory must not run."),
            out object? context,
            out var lazyScope));
        Assert.Null(scope);
        Assert.Null(context);
        Assert.Null(lazyScope);
    }
}

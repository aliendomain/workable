using System.Collections.Concurrent;
using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Diagnostics")]
public sealed class DiagnosticsAndMetricsEdgeCasesShould
{
    [Fact]
    public void ValidateDurabilitySampleCapacityAndOptionalSampleBoundaries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkSystemDurabilityDiagnosticsTracker(-1));

        var unsampled = new WorkSystemDurabilityDiagnosticsTracker();
        unsampled.RecordClaim(
            1,
            TimeSpan.FromMilliseconds(2),
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow);
        InvokeInstance(
            unsampled,
            "RecordRecentClaimSample",
            1,
            TimeSpan.Zero,
            TimeSpan.Zero,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        Assert.Empty(unsampled.Snapshot(0, TimeSpan.Zero).RecentClaimSamples);

        var sampled = new WorkSystemDurabilityDiagnosticsTracker(2);
        Assert.Empty(sampled.Snapshot(0, TimeSpan.Zero).RecentClaimSamples);
        sampled.RecordClaim(
            1,
            TimeSpan.FromMilliseconds(3),
            startedAt: DateTimeOffset.UtcNow,
            completedAt: null);
        sampled.RecordClaim(
            2,
            TimeSpan.FromMilliseconds(4),
            startedAt: null,
            completedAt: DateTimeOffset.UtcNow);
        Assert.Empty(sampled.Snapshot(0, TimeSpan.Zero).RecentClaimSamples);

        var pending = (ConcurrentDictionary<WorkerId, long>)typeof(WorkSystemDurabilityDiagnosticsTracker)
            .GetField("pendingCleanupQueuedAtUnixTimeMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sampled)!;
        pending[WorkerId.New()] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        Assert.Equal(TimeSpan.Zero, sampled.Snapshot(0, TimeSpan.Zero).OldestPendingCleanupAge);
    }

    [Fact]
    public void QueueDiagnosticsChooseErrorsThenFallbackMessagesAndSeparateAlertableCodes()
    {
        var tracker = new WorkSystemQueueDiagnosticsTracker();
        tracker.RecordRejected(new WorkQueueOutcome(WorkQueueStatus.Invalid, null, []));
        Assert.Null(tracker.Diagnostics.LastRejectedCode);
        Assert.False(WorkSystemQueueDiagnosticsTracker.IsAlertableRejectionCode(null));

        tracker.RecordRejected(new WorkQueueOutcome(
            WorkQueueStatus.Invalid,
            null,
            [WorkMessage.Information("informational", "information")]));
        Assert.Equal("informational", tracker.Diagnostics.LastRejectedCode);

        tracker.RecordRejected(new WorkQueueOutcome(
            WorkQueueStatus.Invalid,
            null,
            [
                WorkMessage.Information("first", "first"),
                WorkMessage.Error("workable.system.capacity_reached", "capacity"),
            ]));
        Assert.Equal("workable.system.capacity_reached", tracker.Diagnostics.LastRejectedCode);
        Assert.Equal(1, tracker.Diagnostics.AlertableRejectedWorkCount);
    }

    [Fact]
    public void MetricsHelpersBoundHistogramsPercentilesAndIntegerProjection()
    {
        Assert.Equal(0, InvokeMetrics<int>("GetExecutionDurationHistogramBucket", 0L));
        Assert.True(InvokeMetrics<int>("GetExecutionDurationHistogramBucket", long.MaxValue) > 0);
        Assert.Equal(
            0d,
            InvokeMetrics<double>(
                "GetExecutionDurationHistogramPercentileMilliseconds",
                0,
                2L,
                2L,
                1d));
        Assert.True(InvokeMetrics<double>(
            "GetExecutionDurationHistogramUpperBoundMilliseconds",
            int.MaxValue) > 0);
        Assert.Equal(int.MaxValue, InvokeMetrics<int>("ToInt32Saturated", (long)int.MaxValue + 1));
        Assert.Equal(-1, InvokeMetrics<int>("ToInt32Saturated", -1L));
    }

    [Fact]
    public void RetryHelpersHandleNoJitterOverflowAndMissingTerminalAttempt()
    {
        var noJitter = new WorkTransientRetryConfiguration
        {
            InitialDelay = TimeSpan.FromSeconds(2),
            MaximumDelay = TimeSpan.FromSeconds(10),
            Jitter = TimeSpan.Zero,
        };
        Assert.Equal(TimeSpan.FromSeconds(2), RetryCapableWorkerExecutionStrategy.GetRetryDelay(noJitter, 1));

        var saturating = noJitter with
        {
            InitialDelay = TimeSpan.MaxValue,
            MaximumDelay = TimeSpan.MaxValue,
            Jitter = TimeSpan.FromTicks(1),
        };
        Assert.Equal(TimeSpan.MaxValue, RetryCapableWorkerExecutionStrategy.GetRetryDelay(saturating, 2));

        var missing = RetryIterationResult.Continue(1);
        var loop = new AttemptLoopResult(Attempt: null, Completion: null);
        Assert.Null(missing.Completion);
        Assert.Throws<InvalidOperationException>(() => _ = loop.RequiredAttempt);
    }

    private static void InvokeInstance(object target, string methodName, params object?[] arguments)
        => target.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    private static T InvokeMetrics<T>(string methodName, params object?[] arguments)
        => (T)typeof(InMemoryWorkMetricsSink)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == methodName && method.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments)!;
}

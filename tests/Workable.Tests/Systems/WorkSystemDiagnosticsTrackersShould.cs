namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class WorkSystemDiagnosticsTrackersShould
{
    [Fact]
    public void QueueTrackerCaptureRejectedOutcomeDetailsAndAlertableCounts()
    {
        var tracker = new WorkSystemQueueDiagnosticsTracker();
        tracker.RecordRejected(WorkQueueOutcome.Invalid(
            [
                WorkMessage.Warning("workable.queue.warning", "A warning."),
                WorkMessage.Error("workable.system.capacity_reached", "Capacity reached."),
            ]));
        tracker.RecordRejected(WorkQueueOutcome.Invalid(
            [WorkMessage.Error("workable.queue.validation", "Validation failed.")]));

        var diagnostics = tracker.Diagnostics;

        Assert.Equal(2, diagnostics.RejectedWorkCount);
        Assert.Equal(WorkQueueStatus.Invalid, diagnostics.LastRejectedStatus);
        Assert.Equal("workable.queue.validation", diagnostics.LastRejectedCode);
        Assert.Equal("Validation failed.", diagnostics.LastRejectedMessage);
        Assert.NotNull(diagnostics.LastRejectedAt);
        Assert.Equal(1, diagnostics.AlertableRejectedWorkCount);
        Assert.Equal("workable.system.capacity_reached", diagnostics.LastAlertableRejectedCode);
        Assert.Equal("Capacity reached.", diagnostics.LastAlertableRejectedMessage);
    }

    [Fact]
    public void QueueTrackerUsesFirstMessageWhenNoErrorMessageExists()
    {
        var tracker = new WorkSystemQueueDiagnosticsTracker();

        tracker.RecordRejected(WorkQueueOutcome.Invalid(
            [
                WorkMessage.Warning("workable.queue.warning", "A warning."),
                WorkMessage.Information("workable.queue.info", "An info message."),
            ]));

        var diagnostics = tracker.Diagnostics;

        Assert.Equal(1, diagnostics.RejectedWorkCount);
        Assert.Equal("workable.queue.warning", diagnostics.LastRejectedCode);
        Assert.Equal("A warning.", diagnostics.LastRejectedMessage);
        Assert.Equal(0, diagnostics.AlertableRejectedWorkCount);
    }

    [Fact]
    public void IdempotencyTrackerCaptureDuplicateRejectionCountAndStorage()
    {
        var tracker = new WorkSystemIdempotencyDiagnosticsTracker();

        tracker.RecordDuplicateRejected(
            WorkDefinitionId.New(),
            new WorkSubjectId("order", "123"),
            WorkCoordinationStorage.Local);
        tracker.RecordDuplicateRejected(
            WorkDefinitionId.New(),
            new WorkSubjectId("order", "456"),
            WorkCoordinationStorage.Persistent);

        var diagnostics = tracker.Diagnostics();

        Assert.Equal(2, diagnostics.DuplicateRejectionCount);
        Assert.Equal(WorkCoordinationStorage.Persistent, diagnostics.LastDuplicateRejectedStorage);
    }

    [Fact]
    public void ConcurrencyTrackerAggregatesDeferredStartsAndTracksLastDrain()
    {
        var tracker = new WorkSystemConcurrencyDiagnosticsTracker();
        var now = DateTimeOffset.UtcNow;

        tracker.RecordDrain(3);
        var diagnostics = tracker.Snapshot(
        [
            new WorkDefinitionConcurrencyDiagnosticsSnapshot(2, now.AddMinutes(-2)),
            new WorkDefinitionConcurrencyDiagnosticsSnapshot(1, now.AddMinutes(-1)),
            new WorkDefinitionConcurrencyDiagnosticsSnapshot(0, null),
        ]);

        Assert.Equal(3, diagnostics.DeferredStartCount);
        Assert.True(diagnostics.OldestDeferredStartAge >= TimeSpan.FromMinutes(2));
        Assert.Equal(3, diagnostics.LastDrainReleasedCount);

        tracker.Clear();
        Assert.Equal(0, tracker.Snapshot([]).LastDrainReleasedCount);
    }

    [Fact]
    public void ConcurrencyTrackerClampsFutureDeferredStartAgeToZero()
    {
        var tracker = new WorkSystemConcurrencyDiagnosticsTracker();

        var diagnostics = tracker.Snapshot(
        [
            new WorkDefinitionConcurrencyDiagnosticsSnapshot(1, DateTimeOffset.UtcNow.AddMinutes(1)),
        ]);

        Assert.Equal(1, diagnostics.DeferredStartCount);
        Assert.Equal(TimeSpan.Zero, diagnostics.OldestDeferredStartAge);
    }

    [Fact]
    public void DurabilityTrackerReportsPendingCleanupAndRemovesCompletedWorkers()
    {
        var tracker = new WorkSystemDurabilityDiagnosticsTracker();
        var first = WorkerId.New();
        var second = WorkerId.New();

        tracker.TrackCleanupQueued(first);
        tracker.TrackCleanupQueued(second);

        var pending = tracker.Snapshot(acceptedWaiterCount: 2, oldestAcceptedWaiterAge: TimeSpan.FromSeconds(3));
        Assert.Equal(2, pending.AcceptedWaiterCount);
        Assert.Equal(TimeSpan.FromSeconds(3), pending.OldestAcceptedWaiterAge);
        Assert.Equal(2, pending.PendingCleanupCount);
        Assert.True(pending.OldestPendingCleanupAge >= TimeSpan.Zero);

        tracker.TrackCleanupCompleted(first);
        Assert.Equal(1, tracker.Snapshot(0, TimeSpan.Zero).PendingCleanupCount);

        tracker.TrackCleanupCompleted([second]);
        Assert.Equal(0, tracker.Snapshot(0, TimeSpan.Zero).PendingCleanupCount);
    }

    [Fact]
    public void DurabilityTrackerReportsClaimMetrics()
    {
        var tracker = new WorkSystemDurabilityDiagnosticsTracker();

        tracker.RecordClaim(
            claimedEntryCount: 3,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(4));
        tracker.RecordClaim(
            claimedEntryCount: 0,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(1));

        var diagnostics = tracker.Snapshot(0, TimeSpan.Zero);
        Assert.Equal(2, diagnostics.ClaimAttemptCount);
        Assert.Equal(3, diagnostics.ClaimedEntryCount);
        Assert.Equal(1, diagnostics.EmptyClaimCount);
        Assert.Equal(0, diagnostics.LastClaimedEntryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(15), diagnostics.TotalClaimElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(5), diagnostics.LastClaimElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(10), diagnostics.MaxClaimElapsed);
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.FromMilliseconds(15).Ticks / 2), diagnostics.AverageClaimElapsed);
        Assert.Equal(1.5, diagnostics.AverageClaimedEntries);
        Assert.Equal(200, diagnostics.ClaimedEntriesPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(5), diagnostics.TotalClaimAcceptanceElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(1), diagnostics.LastClaimAcceptanceElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(4), diagnostics.MaxClaimAcceptanceElapsed);
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.FromMilliseconds(5).Ticks / 2), diagnostics.AverageClaimAcceptanceElapsed);
        Assert.Equal(600, diagnostics.ClaimAcceptanceEntriesPerSecond);
        Assert.Empty(diagnostics.RecentClaimSamples);
    }

    [Fact]
    public void DurabilityTrackerRetainsBoundedRecentClaimSamplesWhenEnabled()
    {
        var tracker = new WorkSystemDurabilityDiagnosticsTracker(recentClaimSampleCapacity: 2);
        var startedAt = DateTimeOffset.UtcNow;

        tracker.RecordClaim(
            claimedEntryCount: 1,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(1),
            startedAt,
            startedAt.AddMilliseconds(11));
        tracker.RecordClaim(
            claimedEntryCount: 2,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(2),
            startedAt.AddMilliseconds(20),
            startedAt.AddMilliseconds(42));
        tracker.RecordClaim(
            claimedEntryCount: 0,
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(3),
            startedAt.AddMilliseconds(50),
            startedAt.AddMilliseconds(83));

        var samples = tracker.Snapshot(0, TimeSpan.Zero).RecentClaimSamples;

        Assert.Equal(2, samples.Count);
        Assert.Equal(2, samples[0].Sequence);
        Assert.Equal(2, samples[0].ClaimedEntryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), samples[0].ClaimElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(2), samples[0].AcceptanceElapsed);
        Assert.Equal(3, samples[1].Sequence);
        Assert.Equal(0, samples[1].ClaimedEntryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(30), samples[1].ClaimElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(3), samples[1].AcceptanceElapsed);
    }

    [Fact]
    public void DurabilityTrackerCapturesAndClearsLoopFailures()
    {
        var tracker = new WorkSystemDurabilityDiagnosticsTracker();

        tracker.RecordReaderFailure(new InvalidOperationException("reader failed"));
        tracker.RecordLeaseRenewalFailure(new TimeoutException("lease failed"));
        tracker.RecordCleanupFailure(new ApplicationException("cleanup failed"));

        var failed = tracker.Snapshot(0, TimeSpan.Zero);
        Assert.True(failed.HasReaderFailure);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.ReaderFailureType);
        Assert.Equal("reader failed", failed.ReaderFailureMessage);
        Assert.True(failed.HasLeaseRenewalFailure);
        Assert.Equal(typeof(TimeoutException).FullName, failed.LeaseRenewalFailureType);
        Assert.Equal("lease failed", failed.LeaseRenewalFailureMessage);
        Assert.True(failed.HasCleanupFailure);
        Assert.Equal(typeof(ApplicationException).FullName, failed.CleanupFailureType);
        Assert.Equal("cleanup failed", failed.CleanupFailureMessage);

        tracker.RecordReaderSuccess();
        tracker.RecordLeaseRenewalSuccess();
        tracker.RecordCleanupSuccess();

        var cleared = tracker.Snapshot(0, TimeSpan.Zero);
        Assert.False(cleared.HasReaderFailure);
        Assert.False(cleared.HasLeaseRenewalFailure);
        Assert.False(cleared.HasCleanupFailure);
    }
}

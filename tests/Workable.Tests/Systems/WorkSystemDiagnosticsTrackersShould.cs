namespace Workable.Tests;

public sealed class WorkSystemDiagnosticsTrackersShould
{
    [Fact]
    public void QueueTrackerCaptureRejectedOutcomeDetailsAndAlertableCounts()
    {
        var tracker = new WorkSystemQueueDiagnosticsTracker();
        var definitionId = WorkDefinitionId.New();

        tracker.RecordRejected(WorkQueueOutcome.Invalid(
            definitionId,
            [
                WorkMessage.Warning("workable.queue.warning", "A warning."),
                WorkMessage.Error("workable.system.capacity_reached", "Capacity reached."),
            ]));
        tracker.RecordRejected(WorkQueueOutcome.Invalid(
            definitionId,
            [WorkMessage.Error("workable.queue.validation", "Validation failed.")]));

        var diagnostics = tracker.Diagnostics;

        Assert.Equal(2, diagnostics.RejectedWorkCount);
        Assert.Equal(WorkQueueStatus.Invalid, diagnostics.LastRejectedStatus);
        Assert.Equal(definitionId, diagnostics.LastRejectedDefinitionId);
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
            null,
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

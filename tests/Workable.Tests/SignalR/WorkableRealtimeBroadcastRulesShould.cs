namespace Workable.Tests;

public sealed class WorkableRealtimeBroadcastRulesShould
{
    [Fact]
    public void SkipStateBasedViewWhenReadModelSequenceIsUnchanged()
    {
        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishView(
            requiresIntervalPublish: false,
            lastPublishedSequence: 42,
            appliedSequence: 42);

        Assert.False(shouldPublish);
    }

    [Fact]
    public void PublishStateBasedViewWhenReadModelSequenceChanges()
    {
        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishView(
            requiresIntervalPublish: false,
            lastPublishedSequence: 42,
            appliedSequence: 43);

        Assert.True(shouldPublish);
    }

    [Fact]
    public void PublishIntervalViewWhenReadModelSequenceIsUnchanged()
    {
        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishView(
            requiresIntervalPublish: true,
            lastPublishedSequence: 42,
            appliedSequence: 42);

        Assert.True(shouldPublish);
    }

    [Fact]
    public void SkipInitialHealthyDiagnosticsAlertState()
    {
        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishDiagnosticsAlertChange(
            previous: null,
            current: AlertState());

        Assert.False(shouldPublish);
    }

    [Fact]
    public void SkipUnchangedDiagnosticsAlertState()
    {
        var state = AlertState(alertableRejectedWorkCount: 1);

        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishDiagnosticsAlertChange(
            previous: state,
            current: state);

        Assert.False(shouldPublish);
    }

    [Fact]
    public void PublishInitialAlertingDiagnosticsAlertState()
    {
        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishDiagnosticsAlertChange(
            previous: null,
            current: AlertState(alertableRejectedWorkCount: 1));

        Assert.True(shouldPublish);
    }

    [Fact]
    public void PublishChangedDiagnosticsAlertStateToClearExistingAlert()
    {
        var shouldPublish = WorkableRealtimeBroadcastRules.ShouldPublishDiagnosticsAlertChange(
            previous: AlertState(alertableRejectedWorkCount: 1),
            current: AlertState());

        Assert.True(shouldPublish);
    }

    [Fact]
    public void TreatNonAlertableRejectedWorkAsHealthy()
    {
        var state = AlertState(
            rejectedWorkCount: 1,
            lastRejectedCode: "workable.queue.validation",
            lastRejectedMessage: "Validation failed.");

        Assert.True(state.HasRejectedWork);
        Assert.False(state.HasAlertableRejectedWork);
        Assert.False(state.IsAlerting);
    }

    [Theory]
    [MemberData(nameof(DiagnosticAlertingStates))]
    public void TreatDiagnosticsProblemsAsAlerting(object candidate)
    {
        var state = Assert.IsType<WorkableRealtimeDiagnosticsAlertState>(candidate);

        Assert.True(state.IsAlerting);
    }

    public static IEnumerable<object[]> DiagnosticAlertingStates()
    {
        yield return [AlertState(alertableRejectedWorkCount: 1)];
        yield return [AlertState(readModelLagSeverity: WorkableRealtimeDiagnosticsLagSeverity.Warning)];
        yield return [AlertState(hasProjectorFailure: true)];
        yield return [AlertState(retentionLagSeverity: WorkableRealtimeDiagnosticsLagSeverity.Warning)];
        yield return [AlertState(hasSchedulerFailure: true)];
        yield return [AlertState(concurrencyLagSeverity: WorkableRealtimeDiagnosticsLagSeverity.Warning)];
        yield return [AlertState(acceptedWorkerLagSeverity: WorkableRealtimeDiagnosticsLagSeverity.Warning)];
        yield return [AlertState(cleanupLagSeverity: WorkableRealtimeDiagnosticsLagSeverity.Warning)];
        yield return [AlertState(hasReaderFailure: true)];
        yield return [AlertState(hasLeaseRenewalFailure: true)];
        yield return [AlertState(hasCleanupFailure: true)];
        yield return [AlertState(systemState: WorkSystemState.Stopping)];
    }

    private static WorkableRealtimeDiagnosticsAlertState AlertState(
        WorkSystemState systemState = WorkSystemState.Started,
        long rejectedWorkCount = 0,
        string? lastRejectedCode = null,
        string? lastRejectedMessage = null,
        long alertableRejectedWorkCount = 0,
        WorkableRealtimeDiagnosticsLagSeverity readModelLagSeverity = WorkableRealtimeDiagnosticsLagSeverity.Normal,
        bool hasProjectorFailure = false,
        WorkableRealtimeDiagnosticsLagSeverity retentionLagSeverity = WorkableRealtimeDiagnosticsLagSeverity.Normal,
        bool hasSchedulerFailure = false,
        WorkableRealtimeDiagnosticsLagSeverity concurrencyLagSeverity = WorkableRealtimeDiagnosticsLagSeverity.Normal,
        WorkableRealtimeDiagnosticsLagSeverity acceptedWorkerLagSeverity = WorkableRealtimeDiagnosticsLagSeverity.Normal,
        WorkableRealtimeDiagnosticsLagSeverity cleanupLagSeverity = WorkableRealtimeDiagnosticsLagSeverity.Normal,
        bool hasReaderFailure = false,
        bool hasLeaseRenewalFailure = false,
        bool hasCleanupFailure = false)
        => new(
            SystemName: "system",
            SystemState: systemState,
            RejectedWorkCount: Math.Max(rejectedWorkCount, alertableRejectedWorkCount),
            LastRejectedAt: Math.Max(rejectedWorkCount, alertableRejectedWorkCount) > 0
                ? DateTimeOffset.UnixEpoch
                : null,
            LastRejectedCode: lastRejectedCode ?? (alertableRejectedWorkCount > 0
                ? "workable.system.capacity_reached"
                : null),
            LastRejectedMessage: lastRejectedMessage ?? (alertableRejectedWorkCount > 0
                ? "Capacity reached."
                : null),
            AlertableRejectedWorkCount: alertableRejectedWorkCount,
            LastAlertableRejectedCode: alertableRejectedWorkCount > 0
                ? "workable.system.capacity_reached"
                : null,
            LastAlertableRejectedMessage: alertableRejectedWorkCount > 0
                ? "Capacity reached."
                : null,
            ReadModelPendingUpdateCount: readModelLagSeverity == WorkableRealtimeDiagnosticsLagSeverity.Normal ? 0 : 1,
            ReadModelWarningThreshold: 1,
            ReadModelLagSeverity: readModelLagSeverity,
            HasProjectorFailure: hasProjectorFailure,
            ProjectorFailureType: hasProjectorFailure ? typeof(InvalidOperationException).FullName : null,
            ProjectorFailureMessage: hasProjectorFailure ? "Projector failed." : null,
            TrackedFinalWorkerCount: 0,
            ScheduledPurgeCount: 0,
            OldestDuePurgeAge: TimeSpan.Zero,
            RetentionWarningSeconds: 30,
            RetentionLagSeverity: retentionLagSeverity,
            HasSchedulerFailure: hasSchedulerFailure,
            SchedulerFailureType: hasSchedulerFailure ? typeof(InvalidOperationException).FullName : null,
            SchedulerFailureMessage: hasSchedulerFailure ? "Scheduler failed." : null,
            DeferredStartCount: concurrencyLagSeverity == WorkableRealtimeDiagnosticsLagSeverity.Normal ? 0 : 1,
            OldestDeferredStartAge: TimeSpan.Zero,
            LastDrainReleasedCount: 0,
            ConcurrencyWarningSeconds: 30,
            ConcurrencyLagSeverity: concurrencyLagSeverity,
            AcceptedWaiterCount: acceptedWorkerLagSeverity == WorkableRealtimeDiagnosticsLagSeverity.Normal ? 0 : 1,
            OldestAcceptedWaiterAge: TimeSpan.Zero,
            AcceptedWorkerWarningSeconds: 30,
            AcceptedWorkerLagSeverity: acceptedWorkerLagSeverity,
            PendingCleanupCount: cleanupLagSeverity == WorkableRealtimeDiagnosticsLagSeverity.Normal ? 0 : 1,
            OldestPendingCleanupAge: TimeSpan.Zero,
            CleanupWarningSeconds: 30,
            CleanupLagSeverity: cleanupLagSeverity,
            HasReaderFailure: hasReaderFailure,
            ReaderFailureType: hasReaderFailure ? typeof(InvalidOperationException).FullName : null,
            ReaderFailureMessage: hasReaderFailure ? "Reader failed." : null,
            HasLeaseRenewalFailure: hasLeaseRenewalFailure,
            LeaseRenewalFailureType: hasLeaseRenewalFailure ? typeof(TimeoutException).FullName : null,
            LeaseRenewalFailureMessage: hasLeaseRenewalFailure ? "Lease renewal failed." : null,
            HasCleanupFailure: hasCleanupFailure,
            CleanupFailureType: hasCleanupFailure ? typeof(ApplicationException).FullName : null,
            CleanupFailureMessage: hasCleanupFailure ? "Cleanup failed." : null);
}

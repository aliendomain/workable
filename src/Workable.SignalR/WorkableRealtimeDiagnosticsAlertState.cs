namespace Workable;

internal enum WorkableRealtimeDiagnosticsLagSeverity
{
    Normal,
    Warning,
    Critical,
}

internal sealed record WorkableRealtimeDiagnosticsAlertState(
    string? SystemName,
    WorkSystemState SystemState,
    long RejectedWorkCount,
    DateTimeOffset? LastRejectedAt,
    string? LastRejectedCode,
    string? LastRejectedMessage,
    long AlertableRejectedWorkCount,
    string? LastAlertableRejectedCode,
    string? LastAlertableRejectedMessage,
    long ReadModelPendingUpdateCount,
    int ReadModelWarningThreshold,
    WorkableRealtimeDiagnosticsLagSeverity ReadModelLagSeverity,
    bool HasProjectorFailure,
    string? ProjectorFailureType,
    string? ProjectorFailureMessage,
    int TrackedFinalWorkerCount,
    int ScheduledPurgeCount,
    TimeSpan OldestDuePurgeAge,
    int RetentionWarningSeconds,
    WorkableRealtimeDiagnosticsLagSeverity RetentionLagSeverity,
    bool HasSchedulerFailure,
    string? SchedulerFailureType,
    string? SchedulerFailureMessage,
    int DeferredStartCount,
    TimeSpan OldestDeferredStartAge,
    int LastDrainReleasedCount,
    int ConcurrencyWarningSeconds,
    WorkableRealtimeDiagnosticsLagSeverity ConcurrencyLagSeverity,
    int AcceptedWaiterCount,
    TimeSpan OldestAcceptedWaiterAge,
    int AcceptedWorkerWarningSeconds,
    WorkableRealtimeDiagnosticsLagSeverity AcceptedWorkerLagSeverity,
    int PendingCleanupCount,
    TimeSpan OldestPendingCleanupAge,
    int CleanupWarningSeconds,
    WorkableRealtimeDiagnosticsLagSeverity CleanupLagSeverity,
    bool HasReaderFailure,
    string? ReaderFailureType,
    string? ReaderFailureMessage,
    bool HasLeaseRenewalFailure,
    string? LeaseRenewalFailureType,
    string? LeaseRenewalFailureMessage,
    bool HasCleanupFailure,
    string? CleanupFailureType,
    string? CleanupFailureMessage)
{
    public bool IsShuttingDown => this.SystemState == WorkSystemState.Stopping;

    public bool HasRejectedWork => this.RejectedWorkCount > 0;

    public bool HasAlertableRejectedWork => this.AlertableRejectedWorkCount > 0;

    public bool IsReadModelBehind => this.ReadModelLagSeverity != WorkableRealtimeDiagnosticsLagSeverity.Normal;

    public bool IsRetentionBehind => this.RetentionLagSeverity != WorkableRealtimeDiagnosticsLagSeverity.Normal;

    public bool IsConcurrencyBehind => this.ConcurrencyLagSeverity != WorkableRealtimeDiagnosticsLagSeverity.Normal;

    public bool IsAcceptedWorkerMaterializationBehind
        => this.AcceptedWorkerLagSeverity != WorkableRealtimeDiagnosticsLagSeverity.Normal;

    public bool IsCleanupBehind => this.CleanupLagSeverity != WorkableRealtimeDiagnosticsLagSeverity.Normal;

    public bool IsAlerting =>
        this.HasAlertableRejectedWork ||
        this.IsReadModelBehind ||
        this.HasProjectorFailure ||
        this.IsRetentionBehind ||
        this.HasSchedulerFailure ||
        this.IsConcurrencyBehind ||
        this.IsAcceptedWorkerMaterializationBehind ||
        this.IsCleanupBehind ||
        this.HasReaderFailure ||
        this.HasLeaseRenewalFailure ||
        this.HasCleanupFailure ||
        this.IsShuttingDown;
}

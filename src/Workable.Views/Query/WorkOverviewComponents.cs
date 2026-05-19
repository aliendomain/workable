namespace Workable;

public sealed record WorkOverviewWorkersCompactComponent(
    int ActiveWorkerCount,
    int FailedWorkerCount,
    DateTimeOffset? OldestQueuedAt);

public sealed record WorkOverviewWorkersStandardComponent(
    int DefinitionCount,
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    DateTimeOffset? OldestQueuedAt);

public sealed record WorkOverviewFailedWorkerStandard(
    WorkerId Id,
    string DefinitionName,
    long Revision,
    DateTimeOffset UpdatedAt,
    TimeSpan TotalExecutionDuration);

public sealed record WorkOverviewFailedWorkerDetailed(
    WorkerId Id,
    string DefinitionName,
    long Revision,
    WorkerState State,
    DateTimeOffset UpdatedAt,
    TimeSpan TotalExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlySet<WorkIdentifier> Identifiers);

public sealed record WorkViewWorkerGridDetailedComponent(
    IReadOnlyList<WorkViewWorkerGridDetailed> Workers,
    int TotalCount,
    int Skip,
    int Take);

public sealed record WorkViewWorkerGridDetailed(
    WorkerId Id,
    string DefinitionName,
    long Revision,
    WorkerState State,
    DateTimeOffset UpdatedAt,
    TimeSpan TotalExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlySet<WorkIdentifier> Identifiers);

public sealed record WorkOverviewIterationsCompactComponent(
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus);

public sealed record WorkOverviewIterationsStandardComponent(
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus,
    IReadOnlyList<WorkIterationKeyTypeFacet> CommonKeyTypes);

public sealed record WorkOverviewThroughputCompactComponent(
    int ActiveWorkerCount,
    WorkOverviewThroughputCompact Throughput);

public sealed record WorkOverviewThroughputCompact(
    int WindowSeconds,
    int SettledCount,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkOverviewThroughputLiveSummary LiveSummary);

public sealed record WorkOverviewThroughputStandardComponent(
    int ActiveWorkerCount,
    WorkOverviewThroughputStandard Throughput);

public sealed record WorkOverviewThroughputStandard(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowSeconds,
    int BucketSeconds,
    int SettledCount,
    IReadOnlyList<WorkOverviewThroughputBucket> Buckets,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkOverviewThroughputLiveSummary LiveSummary);

public sealed record WorkOverviewThroughputLiveSummary(
    int RateWindowSeconds,
    double StartedPerSecond,
    double CompletedPerSecond,
    double FailedPerSecond,
    double CanceledPerSecond,
    double InFlightDeltaPerSecond);

public sealed record WorkOverviewThroughputBucket(
    DateTimeOffset At,
    int Started,
    int Completed,
    int Failed,
    int Canceled,
    double AverageExecutionMilliseconds);

public sealed record WorkOverviewIterationStandard(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration);

public sealed record WorkOverviewIterationDetailed(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    WorkerState WorkerState,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlyCollection<WorkIdentifier> Identifiers);

public sealed record WorkViewIterationGridDetailedComponent(
    IReadOnlyList<WorkViewIterationGridDetailed> Iterations,
    int TotalCount,
    int Skip,
    int Take);

public sealed record WorkViewIterationGridDetailed(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    WorkerState WorkerState,
    WorkCompletionStatus Status,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlyCollection<WorkIdentifier> Identifiers);

public sealed record WorkQueueDiagnosticsCompactComponent(
    long RejectedWorkCount,
    bool HasRejectedWork,
    DateTimeOffset? LastRejectedAt,
    string? LastRejectedCode,
    string? LastRejectedMessage,
    long AlertableRejectedWorkCount,
    bool HasAlertableRejectedWork,
    string? LastAlertableRejectedCode,
    string? LastAlertableRejectedMessage);

public sealed record WorkQueueDiagnosticsDetailedComponent(
    WorkSystemQueueDiagnostics Queue,
    bool HasRejectedWork);

public sealed record WorkSystemDiagnosticsCompactComponent(
    string? SystemName,
    WorkSystemState SystemState,
    bool IsShuttingDown);

public sealed record WorkReadModelDiagnosticsCompactComponent(
    long PendingUpdateCount,
    bool IsReadModelBehind,
    int ReadModelLagWarningThreshold,
    bool HasProjectorFailure,
    string? ProjectorFailureType,
    string? ProjectorFailureMessage);

public sealed record WorkReadModelDiagnosticsDetailedComponent(
    WorkSystemReadModelDiagnostics ReadModel,
    bool IsReadModelBehind,
    int ReadModelLagWarningThreshold);

public sealed record WorkRetentionDiagnosticsCompactComponent(
    int TrackedFinalWorkerCount,
    int ScheduledPurgeCount,
    TimeSpan OldestDuePurgeAge,
    bool IsRetentionBehind,
    int RetentionLagWarningSeconds,
    bool HasSchedulerFailure,
    string? SchedulerFailureType,
    string? SchedulerFailureMessage);

public sealed record WorkRetentionDiagnosticsDetailedComponent(
    WorkSystemRetentionDiagnostics Retention,
    bool IsRetentionBehind,
    int RetentionLagWarningSeconds);

public sealed record WorkConcurrencyDiagnosticsCompactComponent(
    int DeferredStartCount,
    TimeSpan OldestDeferredStartAge,
    int LastDrainReleasedCount,
    bool IsConcurrencyBehind,
    int ConcurrencyLagWarningSeconds);

public sealed record WorkConcurrencyDiagnosticsDetailedComponent(
    WorkSystemConcurrencyDiagnostics Concurrency,
    bool IsConcurrencyBehind,
    int ConcurrencyLagWarningSeconds);

public sealed record WorkDurabilityDiagnosticsCompactComponent(
    int AcceptedWaiterCount,
    TimeSpan OldestAcceptedWaiterAge,
    int PendingCleanupCount,
    TimeSpan OldestPendingCleanupAge,
    bool IsAcceptedWorkerMaterializationBehind,
    int AcceptedWorkerWarningSeconds,
    bool IsCleanupBehind,
    int CleanupWarningSeconds,
    bool HasReaderFailure,
    string? ReaderFailureType,
    string? ReaderFailureMessage,
    bool HasLeaseRenewalFailure,
    string? LeaseRenewalFailureType,
    string? LeaseRenewalFailureMessage,
    bool HasCleanupFailure,
    string? CleanupFailureType,
    string? CleanupFailureMessage);

public sealed record WorkDurabilityDiagnosticsDetailedComponent(
    WorkSystemDurabilityDiagnostics Durability,
    bool IsAcceptedWorkerMaterializationBehind,
    int AcceptedWorkerWarningSeconds,
    bool IsCleanupBehind,
    int CleanupWarningSeconds);

public sealed record WorkIdempotencyDiagnosticsCompactComponent(
    long DuplicateRejectionCount,
    string? LastDuplicateRejectedStorage);

public sealed record WorkIdempotencyDiagnosticsDetailedComponent(
    WorkSystemIdempotencyDiagnostics Idempotency);

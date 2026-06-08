namespace Workable;

/// <summary>
/// Compact overview component for active and failed worker counts.
/// </summary>
public sealed record WorkOverviewWorkersCompactComponent(
    int ActiveWorkerCount,
    int FailedWorkerCount,
    DateTimeOffset? OldestQueuedAt);

/// <summary>
/// Standard overview component for worker counts and worker-state distribution.
/// </summary>
public sealed record WorkOverviewWorkersStandardComponent(
    int DefinitionCount,
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    DateTimeOffset? OldestQueuedAt);

/// <summary>
/// Standard failed-worker row used by overview-style components.
/// </summary>
public sealed record WorkOverviewFailedWorkerStandard(
    WorkerId Id,
    string DefinitionName,
    long Revision,
    DateTimeOffset UpdatedAt,
    TimeSpan TotalExecutionDuration);

/// <summary>
/// Detailed failed-worker row used by overview-style components.
/// </summary>
public sealed record WorkOverviewFailedWorkerDetailed(
    WorkerId Id,
    string DefinitionName,
    long Revision,
    WorkerState State,
    DateTimeOffset UpdatedAt,
    TimeSpan TotalExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlySet<WorkIdentifier> Identifiers);

/// <summary>
/// Detailed worker-grid component.
/// </summary>
public sealed record WorkViewWorkerGridDetailedComponent(
    IReadOnlyList<WorkViewWorkerGridDetailed> Workers,
    int TotalCount,
    int Skip,
    int Take);

/// <summary>
/// Detailed worker row used by the worker-grid component.
/// </summary>
public sealed record WorkViewWorkerGridDetailed(
    WorkerId Id,
    string DefinitionName,
    long Revision,
    WorkerState State,
    bool IsFinal,
    DateTimeOffset UpdatedAt,
    TimeSpan TotalExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlySet<WorkIdentifier> Identifiers);

/// <summary>
/// Compact overview component for iteration status counts.
/// </summary>
public sealed record WorkOverviewIterationsCompactComponent(
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus);

/// <summary>
/// Standard overview component for iteration status counts and common key facets.
/// </summary>
public sealed record WorkOverviewIterationsStandardComponent(
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus,
    IReadOnlyList<WorkIterationKeyTypeFacet> CommonKeyTypes);

/// <summary>
/// Compact throughput component with active-worker count and window summary.
/// </summary>
public sealed record WorkOverviewThroughputCompactComponent(
    int ActiveWorkerCount,
    WorkOverviewThroughputCompact Throughput);

/// <summary>
/// Compact throughput summary for one time window.
/// </summary>
public sealed record WorkOverviewThroughputCompact(
    int WindowSeconds,
    int SettledCount,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkOverviewThroughputLiveSummary LiveSummary);

/// <summary>
/// Standard throughput component with active-worker count and bucketed throughput.
/// </summary>
public sealed record WorkOverviewThroughputStandardComponent(
    int ActiveWorkerCount,
    WorkOverviewThroughputStandard Throughput);

/// <summary>
/// Standard throughput summary with bucketed history across the requested window.
/// </summary>
public sealed record WorkOverviewThroughputStandard(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowSeconds,
    int BucketSeconds,
    int SettledCount,
    IReadOnlyList<WorkOverviewThroughputBucket> Buckets,
    WorkThroughputExecutionSummary ExecutionSummary,
    WorkOverviewThroughputLiveSummary LiveSummary);

/// <summary>
/// Live in-flight and rate summary included in throughput components.
/// </summary>
public sealed record WorkOverviewThroughputLiveSummary(
    int RateWindowSeconds,
    double StartedPerSecond,
    double CompletedPerSecond,
    double FailedPerSecond,
    double CanceledPerSecond,
    double InFlightDeltaPerSecond);

/// <summary>
/// One throughput bucket in the standard throughput component.
/// </summary>
public sealed record WorkOverviewThroughputBucket(
    DateTimeOffset At,
    int Started,
    int Completed,
    int Failed,
    int Canceled,
    double AverageExecutionMilliseconds);

/// <summary>
/// Standard completed-iteration row used by overview-style components.
/// </summary>
public sealed record WorkOverviewIterationStandard(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration);

/// <summary>
/// Detailed completed-iteration row used by overview-style components.
/// </summary>
public sealed record WorkOverviewIterationDetailed(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    WorkerState WorkerState,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlyCollection<WorkIdentifier> Identifiers);

/// <summary>
/// Detailed iteration-grid component.
/// </summary>
public sealed record WorkViewIterationGridDetailedComponent(
    IReadOnlyList<WorkViewIterationGridDetailed> Iterations,
    int TotalCount,
    int Skip,
    int Take);

/// <summary>
/// Detailed iteration row used by the iteration-grid component.
/// </summary>
public sealed record WorkViewIterationGridDetailed(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    WorkerState WorkerState,
    WorkCompletionStatus Status,
    bool IsFinal,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    IReadOnlyCollection<WorkIdentifier> Identifiers);

/// <summary>
/// Compact queue diagnostics component.
/// </summary>
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

/// <summary>
/// Detailed queue diagnostics component.
/// </summary>
public sealed record WorkQueueDiagnosticsDetailedComponent(
    WorkSystemQueueDiagnostics Queue,
    bool HasRejectedWork);

/// <summary>
/// Compact system diagnostics component.
/// </summary>
public sealed record WorkSystemDiagnosticsCompactComponent(
    string? SystemName,
    WorkSystemState SystemState,
    bool IsShuttingDown);

/// <summary>
/// Compact read-model diagnostics component.
/// </summary>
public sealed record WorkReadModelDiagnosticsCompactComponent(
    long PendingUpdateCount,
    bool IsReadModelBehind,
    int ReadModelLagWarningThreshold,
    bool HasProjectorFailure,
    string? ProjectorFailureType,
    string? ProjectorFailureMessage);

/// <summary>
/// Detailed read-model diagnostics component.
/// </summary>
public sealed record WorkReadModelDiagnosticsDetailedComponent(
    WorkSystemReadModelDiagnostics ReadModel,
    bool IsReadModelBehind,
    int ReadModelLagWarningThreshold);

/// <summary>
/// Compact retention diagnostics component.
/// </summary>
public sealed record WorkRetentionDiagnosticsCompactComponent(
    int TrackedFinalWorkerCount,
    int ScheduledPurgeCount,
    TimeSpan OldestDuePurgeAge,
    bool IsRetentionBehind,
    int RetentionLagWarningSeconds,
    bool HasSchedulerFailure,
    string? SchedulerFailureType,
    string? SchedulerFailureMessage);

/// <summary>
/// Detailed retention diagnostics component.
/// </summary>
public sealed record WorkRetentionDiagnosticsDetailedComponent(
    WorkSystemRetentionDiagnostics Retention,
    bool IsRetentionBehind,
    int RetentionLagWarningSeconds);

/// <summary>
/// Compact concurrency diagnostics component.
/// </summary>
public sealed record WorkConcurrencyDiagnosticsCompactComponent(
    int DeferredStartCount,
    TimeSpan OldestDeferredStartAge,
    int LastDrainReleasedCount,
    bool IsConcurrencyBehind,
    int ConcurrencyLagWarningSeconds);

/// <summary>
/// Detailed concurrency diagnostics component.
/// </summary>
public sealed record WorkConcurrencyDiagnosticsDetailedComponent(
    WorkSystemConcurrencyDiagnostics Concurrency,
    bool IsConcurrencyBehind,
    int ConcurrencyLagWarningSeconds);

/// <summary>
/// Compact durability diagnostics component.
/// </summary>
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

/// <summary>
/// Detailed durability diagnostics component.
/// </summary>
public sealed record WorkDurabilityDiagnosticsDetailedComponent(
    WorkSystemDurabilityDiagnostics Durability,
    bool IsAcceptedWorkerMaterializationBehind,
    int AcceptedWorkerWarningSeconds,
    bool IsCleanupBehind,
    int CleanupWarningSeconds);

/// <summary>
/// Compact idempotency diagnostics component.
/// </summary>
public sealed record WorkIdempotencyDiagnosticsCompactComponent(
    long DuplicateRejectionCount,
    string? LastDuplicateRejectedStorage);

/// <summary>
/// Detailed idempotency diagnostics component.
/// </summary>
public sealed record WorkIdempotencyDiagnosticsDetailedComponent(
    WorkSystemIdempotencyDiagnostics Idempotency);

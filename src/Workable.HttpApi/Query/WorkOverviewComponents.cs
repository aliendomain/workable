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

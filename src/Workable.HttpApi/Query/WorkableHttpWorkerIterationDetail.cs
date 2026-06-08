namespace Workable;

/// <summary>
/// Represents the HTTP worker-iteration detail payload used by iteration detail screens.
/// </summary>
/// <param name="WorkerId">The identifier of the owning worker.</param>
/// <param name="DefinitionName">The definition name of the owning worker.</param>
/// <param name="SubjectId">The worker's primary subject identifier, when one exists.</param>
/// <param name="ConcurrencyKey">The worker's concurrency grouping key, when one exists.</param>
/// <param name="Identifiers">The worker's additional identifiers.</param>
/// <param name="Input">The retained worker input payload, when one exists.</param>
/// <param name="Iteration">The projected iteration snapshot for the selected sequence.</param>
/// <param name="MessageSummary">The compact retained-message severity summary for the iteration.</param>
/// <param name="Logs">The initial retained-log section for the iteration.</param>
public sealed record WorkableHttpWorkerIterationDetail(
    WorkerId WorkerId,
    string DefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    WorkInput? Input,
    WorkableHttpWorkerIterationSnapshot Iteration,
    WorkIterationMessageSummary MessageSummary,
    WorkIterationLogSection Logs);

/// <summary>
/// Represents the compact iteration snapshot embedded in the HTTP iteration-detail payload.
/// </summary>
/// <param name="Sequence">The monotonic sequence number of the iteration within the worker.</param>
/// <param name="StartedAt">The time the iteration started.</param>
/// <param name="CompletedAt">The time the iteration completed.</param>
/// <param name="ExecutionDuration">The retained execution duration of the iteration.</param>
/// <param name="OccurredAt">The primary timestamp used by iteration history views.</param>
/// <param name="Status">The iteration completion status.</param>
/// <param name="AttemptCount">The retry or recurrence attempt count retained for the iteration.</param>
/// <param name="IsFinal">Whether the iteration reached a final completion status.</param>
/// <param name="Output">The retained iteration output payload, when one exists.</param>
/// <param name="Failure">The derived failure details, when the iteration failed.</param>
/// <param name="Profile">The retained execution profile, when profiling was enabled.</param>
public sealed record WorkableHttpWorkerIterationSnapshot(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    DateTimeOffset OccurredAt,
    WorkCompletionStatus Status,
    int AttemptCount,
    bool IsFinal,
    WorkOutput? Output,
    WorkerIterationFailure? Failure,
    WorkProfileSnapshot? Profile);

namespace Workable;

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

namespace Workable;

public sealed record WorkerIterationOverviewItem(
    WorkerId WorkerId,
    long Sequence,
    WorkDefinitionId DefinitionId,
    string DefinitionName,
    string Category,
    WorkerState WorkerState,
    WorkCompletionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyCollection<WorkIdentifier> Identifiers);

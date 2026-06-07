namespace Workable;

public sealed record WorkerIterationOverviewItem(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    string Category,
    WorkerState WorkerState,
    WorkCompletionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyCollection<WorkIdentifier> Identifiers)
{
    public bool IsFinal => this.Status.IsFinal();
}

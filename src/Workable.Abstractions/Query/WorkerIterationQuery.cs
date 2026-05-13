namespace Workable;

public sealed record WorkerIterationQuery(
    WorkerId? WorkerId = null,
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    string? Category = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    DateTimeOffset? StartedFrom = null,
    DateTimeOffset? StartedTo = null,
    DateTimeOffset? CompletedFrom = null,
    DateTimeOffset? CompletedTo = null,
    WorkerIterationQuerySort Sort = WorkerIterationQuerySort.CompletedAt,
    WorkQuerySortDirection Direction = WorkQuerySortDirection.Descending,
    int Skip = 0,
    int Take = WorkerIterationQuery.DefaultTake)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 50;
}

namespace Workable;

public sealed record WorkerQuery(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlySet<WorkerState>? States = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    WorkerQuerySort Sort = WorkerQuerySort.CreatedAt,
    WorkQuerySortDirection Direction = WorkQuerySortDirection.Descending,
    int Skip = 0,
    int Take = 100);

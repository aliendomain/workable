namespace Workable;

public sealed record WorkableHttpWorkerIterationQuery(
    WorkerId? WorkerId = null,
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    string? Category = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlyList<WorkCompletionStatus>? Statuses = null,
    DateTimeOffset? StartedFrom = null,
    DateTimeOffset? StartedTo = null,
    DateTimeOffset? CompletedFrom = null,
    DateTimeOffset? CompletedTo = null,
    WorkerIterationQuerySort Sort = WorkerIterationQuerySort.CompletedAt,
    WorkQuerySortDirection Direction = WorkQuerySortDirection.Descending,
    int Skip = 0,
    int Take = WorkerIterationQuery.DefaultTake)
{
    public WorkerIterationQuery ToWorkerIterationQuery()
        => new(
            this.WorkerId,
            this.DefinitionId,
            this.DefinitionName,
            this.Category,
            this.SubjectId,
            this.ConcurrencyKey,
            this.Identifier,
            this.Statuses?.ToHashSet(),
            this.StartedFrom,
            this.StartedTo,
            this.CompletedFrom,
            this.CompletedTo,
            this.Sort,
            this.Direction,
            this.Skip,
            this.Take);
}

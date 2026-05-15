namespace Workable;

public sealed record WorkableHttpWorkerIterationCriteria(
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
    WorkerIterationCriteriaSort Sort = WorkerIterationCriteriaSort.CompletedAt,
    WorkCriteriaSortDirection Direction = WorkCriteriaSortDirection.Descending,
    int Skip = 0,
    int Take = WorkerIterationCriteria.DefaultTake)
{
    public WorkerIterationCriteria ToWorkerIterationCriteria()
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

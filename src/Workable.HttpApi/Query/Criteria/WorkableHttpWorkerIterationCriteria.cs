namespace Workable;

public sealed record WorkableHttpWorkerIterationCriteria(
    WorkerId? WorkerId = null,
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
            WorkerId: this.WorkerId,
            DefinitionName: this.DefinitionName,
            Category: this.Category,
            SubjectId: this.SubjectId,
            ConcurrencyKey: this.ConcurrencyKey,
            Identifier: this.Identifier,
            Statuses: this.Statuses?.ToHashSet(),
            StartedFrom: this.StartedFrom,
            StartedTo: this.StartedTo,
            CompletedFrom: this.CompletedFrom,
            CompletedTo: this.CompletedTo,
            Sort: this.Sort,
            Direction: this.Direction,
            Skip: this.Skip,
            Take: this.Take);
}

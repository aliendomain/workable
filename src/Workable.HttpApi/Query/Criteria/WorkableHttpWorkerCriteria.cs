namespace Workable;

public sealed record WorkableHttpWorkerCriteria(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlyList<WorkerState>? States = null,
    WorkerConfigurationCriteria? Configuration = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    WorkerCriteriaSort Sort = WorkerCriteriaSort.CreatedAt,
    WorkCriteriaSortDirection Direction = WorkCriteriaSortDirection.Descending,
    int Skip = 0,
    int Take = WorkerCriteria.DefaultTake,
    string? Category = null,
    bool IncludeSubcategories = true)
{
    public WorkerCriteria ToWorkerCriteria()
        => new(
            this.DefinitionId,
            this.DefinitionName,
            this.SubjectId,
            this.ConcurrencyKey,
            this.Identifier,
            this.States?.ToHashSet(),
            this.Configuration,
            this.CreatedFrom,
            this.CreatedTo,
            this.UpdatedFrom,
            this.UpdatedTo,
            this.Sort,
            this.Direction,
            this.Skip,
            this.Take,
            this.Category,
            this.IncludeSubcategories);
}

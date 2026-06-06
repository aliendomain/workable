namespace Workable;

public sealed record WorkableHttpWorkerCriteria(
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
            DefinitionName: this.DefinitionName,
            SubjectId: this.SubjectId,
            ConcurrencyKey: this.ConcurrencyKey,
            Identifier: this.Identifier,
            States: this.States?.ToHashSet(),
            Configuration: this.Configuration,
            CreatedFrom: this.CreatedFrom,
            CreatedTo: this.CreatedTo,
            UpdatedFrom: this.UpdatedFrom,
            UpdatedTo: this.UpdatedTo,
            Sort: this.Sort,
            Direction: this.Direction,
            Skip: this.Skip,
            Take: this.Take,
            Category: this.Category,
            IncludeSubcategories: this.IncludeSubcategories);
}

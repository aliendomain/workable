namespace Workable;
public sealed record WorkableHttpWorkerQuery(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlyList<WorkerState>? States = null,
    WorkerConfigurationQuery? Configuration = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    WorkerQuerySort Sort = WorkerQuerySort.CreatedAt,
    WorkQuerySortDirection Direction = WorkQuerySortDirection.Descending,
    int Skip = 0,
    int Take = WorkerQuery.DefaultTake)
{
    public WorkerQuery ToWorkerQuery()
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
            this.Take);
}

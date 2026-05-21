namespace Workable;

public sealed record WorkerCriteria(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlySet<WorkerState>? States = null,
    WorkerConfigurationCriteria? Configuration = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    WorkerCriteriaSort Sort = WorkerCriteriaSort.CreatedAt,
    WorkCriteriaSortDirection Direction = WorkCriteriaSortDirection.Descending,
    int Skip = 0,
    int Take = 50,
    string? Category = null,
    bool IncludeSubcategories = true,
    IReadOnlySet<WorkDefinitionId>? DefinitionIds = null)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 50;
}

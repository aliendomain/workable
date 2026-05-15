namespace Workable;

public sealed record WorkQueryScope(
    WorkDefinitionId? DefinitionId = null,
    string? DefinitionName = null,
    string? Category = null,
    bool IncludeSubcategories = true)
{
    public WorkOverviewCriteria ToOverviewCriteria(bool includeThroughput = false)
        => new(
            this.DefinitionId,
            this.DefinitionName,
            this.Category,
            this.IncludeSubcategories,
            includeThroughput);

    public static WorkQueryScope? From(WorkOverviewCriteria? query)
        => query is null
            ? null
            : new WorkQueryScope(
                query.DefinitionId,
                query.DefinitionName,
                query.Category,
                query.IncludeSubcategories);
}

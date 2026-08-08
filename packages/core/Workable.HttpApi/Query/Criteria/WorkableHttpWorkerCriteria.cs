namespace Workable;

/// <summary>
/// Represents the HTTP request shape for worker collection queries.
/// </summary>
/// <param name="DefinitionName">An optional exact definition-name filter.</param>
/// <param name="SubjectId">An optional subject filter.</param>
/// <param name="ConcurrencyKey">An optional concurrency-key filter.</param>
/// <param name="Identifier">An optional identifier filter.</param>
/// <param name="States">Optional worker states to include.</param>
/// <param name="Configuration">Optional effective-configuration filters.</param>
/// <param name="CreatedFrom">An optional lower bound for worker creation time.</param>
/// <param name="CreatedTo">An optional upper bound for worker creation time.</param>
/// <param name="UpdatedFrom">An optional lower bound for worker last-update time.</param>
/// <param name="UpdatedTo">An optional upper bound for worker last-update time.</param>
/// <param name="Sort">The worker sort field.</param>
/// <param name="Direction">The worker sort direction.</param>
/// <param name="Skip">The number of matching rows to skip.</param>
/// <param name="Take">The requested page size.</param>
/// <param name="Category">An optional definition category filter.</param>
/// <param name="IncludeSubcategories">Whether category filtering includes descendant categories.</param>
/// <param name="ActorId">An optional exact identifier for the actor that originated the worker.</param>
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
    bool IncludeSubcategories = true,
    string? ActorId = null)
{
    /// <summary>
    /// Converts the HTTP criteria into the core worker-query contract.
    /// </summary>
    /// <returns>The core worker-query criteria.</returns>
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
            IncludeSubcategories: this.IncludeSubcategories,
            ActorId: this.ActorId);
}

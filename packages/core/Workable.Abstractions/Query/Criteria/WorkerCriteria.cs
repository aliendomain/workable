namespace Workable;

/// <summary>
/// Filters, sorts, and pages worker overview queries.
/// </summary>
/// <param name="DefinitionName">An optional exact definition name filter.</param>
/// <param name="DefinitionNames">Optional exact definition names to include.</param>
/// <param name="SubjectId">An optional exact subject-id filter.</param>
/// <param name="ConcurrencyKey">An optional exact concurrency-key filter.</param>
/// <param name="Identifier">An optional exact identifier filter.</param>
/// <param name="States">Optional worker states to include.</param>
/// <param name="Configuration">Optional effective configuration filters.</param>
/// <param name="CreatedFrom">An optional inclusive lower bound for worker creation time.</param>
/// <param name="CreatedTo">An optional inclusive upper bound for worker creation time.</param>
/// <param name="UpdatedFrom">An optional inclusive lower bound for worker update time.</param>
/// <param name="UpdatedTo">An optional inclusive upper bound for worker update time.</param>
/// <param name="Sort">The worker field used for sorting.</param>
/// <param name="Direction">The sort direction.</param>
/// <param name="Skip">The number of matching rows to skip before returning results.</param>
/// <param name="Take">The requested page size, capped by <see cref="MaximumTake"/>.</param>
/// <param name="Category">An optional category-path filter.</param>
/// <param name="IncludeSubcategories">Whether a category filter should include descendant category paths.</param>
/// <param name="ActorId">
/// An optional identifier for the actor in the worker's original request context. Surrounding whitespace is removed
/// and the remaining value is matched with ordinal comparison. This is a query filter, not an authorization boundary.
/// </param>
public sealed record WorkerCriteria(
    string? DefinitionName = null,
    IReadOnlySet<string>? DefinitionNames = null,
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
    string? ActorId = null)
{
    /// <summary>
    /// The default page size Workable uses when callers omit or pass a non-positive <c>Take</c> value.
    /// </summary>
    public const int DefaultTake = 50;
    /// <summary>
    /// The maximum page size Workable returns for one worker query.
    /// </summary>
    public const int MaximumTake = 50;
}

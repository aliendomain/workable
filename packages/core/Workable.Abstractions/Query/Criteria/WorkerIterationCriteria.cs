namespace Workable;

/// <summary>
/// Filters, sorts, and pages worker-iteration overview queries.
/// </summary>
/// <param name="WorkerId">An optional exact worker-id filter.</param>
/// <param name="DefinitionName">An optional exact definition name filter.</param>
/// <param name="DefinitionNames">Optional exact definition names to include.</param>
/// <param name="Category">An optional category-path filter.</param>
/// <param name="SubjectId">An optional exact subject-id filter.</param>
/// <param name="ConcurrencyKey">An optional exact concurrency-key filter.</param>
/// <param name="Identifier">An optional exact identifier filter.</param>
/// <param name="Statuses">Optional iteration completion statuses to include.</param>
/// <param name="StartedFrom">An optional inclusive lower bound for iteration start time.</param>
/// <param name="StartedTo">An optional inclusive upper bound for iteration start time.</param>
/// <param name="CompletedFrom">An optional inclusive lower bound for iteration completion time.</param>
/// <param name="CompletedTo">An optional inclusive upper bound for iteration completion time.</param>
/// <param name="Sort">The iteration field used for sorting.</param>
/// <param name="Direction">The sort direction.</param>
/// <param name="Skip">The number of matching rows to skip before returning results.</param>
/// <param name="Take">The requested page size, capped by <see cref="MaximumTake"/>.</param>
public sealed record WorkerIterationCriteria(
    WorkerId? WorkerId = null,
    string? DefinitionName = null,
    IReadOnlySet<string>? DefinitionNames = null,
    string? Category = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    DateTimeOffset? StartedFrom = null,
    DateTimeOffset? StartedTo = null,
    DateTimeOffset? CompletedFrom = null,
    DateTimeOffset? CompletedTo = null,
    WorkerIterationCriteriaSort Sort = WorkerIterationCriteriaSort.CompletedAt,
    WorkCriteriaSortDirection Direction = WorkCriteriaSortDirection.Descending,
    int Skip = 0,
    int Take = WorkerIterationCriteria.DefaultTake)
{
    /// <summary>
    /// The default page size Workable uses when callers omit or pass a non-positive <c>Take</c> value.
    /// </summary>
    public const int DefaultTake = 50;
    /// <summary>
    /// The maximum page size Workable returns for one iteration query.
    /// </summary>
    public const int MaximumTake = 50;
}

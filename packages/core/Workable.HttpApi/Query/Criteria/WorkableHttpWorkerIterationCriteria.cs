namespace Workable;

/// <summary>
/// Represents the HTTP request shape for worker-iteration collection queries.
/// </summary>
/// <param name="WorkerId">An optional exact worker-id filter.</param>
/// <param name="DefinitionName">An optional exact definition-name filter.</param>
/// <param name="Category">An optional definition category filter.</param>
/// <param name="SubjectId">An optional subject filter.</param>
/// <param name="ConcurrencyKey">An optional concurrency-key filter.</param>
/// <param name="Identifier">An optional identifier filter.</param>
/// <param name="Statuses">Optional iteration statuses to include.</param>
/// <param name="StartedFrom">An optional lower bound for iteration start time.</param>
/// <param name="StartedTo">An optional upper bound for iteration start time.</param>
/// <param name="CompletedFrom">An optional lower bound for iteration completion time.</param>
/// <param name="CompletedTo">An optional upper bound for iteration completion time.</param>
/// <param name="Sort">The iteration sort field.</param>
/// <param name="Direction">The iteration sort direction.</param>
/// <param name="Skip">The number of matching rows to skip.</param>
/// <param name="Take">The requested page size.</param>
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
    /// <summary>
    /// Converts the HTTP criteria into the core worker-iteration query contract.
    /// </summary>
    /// <returns>The core worker-iteration query criteria.</returns>
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

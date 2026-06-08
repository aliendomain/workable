namespace Workable;

/// <summary>
/// Represents a compact overview row for one retained worker iteration.
/// </summary>
/// <param name="WorkerId">The identifier of the owning worker.</param>
/// <param name="Sequence">The monotonic sequence number of the iteration within the worker.</param>
/// <param name="DefinitionName">The registered definition name that produced the worker.</param>
/// <param name="Category">The category of the registered definition.</param>
/// <param name="WorkerState">The current state of the owning worker.</param>
/// <param name="Status">The completion status of the iteration.</param>
/// <param name="StartedAt">The time the iteration started.</param>
/// <param name="CompletedAt">The time the iteration completed.</param>
/// <param name="ExecutionDuration">The retained execution duration of the iteration.</param>
/// <param name="SubjectId">The optional primary business subject associated with the worker.</param>
/// <param name="ConcurrencyKey">The optional concurrency grouping key associated with the worker.</param>
/// <param name="Identifiers">The additional searchable identifiers associated with the worker.</param>
public sealed record WorkerIterationOverviewItem(
    WorkerId WorkerId,
    long Sequence,
    string DefinitionName,
    string Category,
    WorkerState WorkerState,
    WorkCompletionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyCollection<WorkIdentifier> Identifiers)
{
    /// <summary>
    /// Gets a value indicating whether the iteration reached a final completion status.
    /// </summary>
    public bool IsFinal => this.Status.IsFinal();
}

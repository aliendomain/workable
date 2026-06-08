namespace Workable;

/// <summary>
/// Represents a compact overview row for one worker.
/// </summary>
/// <param name="Id">The worker identifier.</param>
/// <param name="DefinitionName">The registered definition name that produced the worker.</param>
/// <param name="SubjectId">The optional primary business subject associated with the worker.</param>
/// <param name="ConcurrencyKey">The optional concurrency grouping key associated with the worker.</param>
/// <param name="Identifiers">The additional searchable identifiers associated with the worker.</param>
/// <param name="Revision">The optimistic-concurrency revision of the worker snapshot.</param>
/// <param name="Category">The category of the registered definition.</param>
/// <param name="State">The current worker lifecycle state.</param>
/// <param name="InterruptionReason">The interruption reason when the worker was interrupted.</param>
/// <param name="CreatedAt">The time the worker was created.</param>
/// <param name="StateChangedAt">The time the worker last changed state.</param>
/// <param name="UpdatedAt">The time the worker was last updated.</param>
public sealed record WorkerOverviewItem(
    WorkerId Id,
    string DefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    long Revision,
    string Category,
    WorkerState State,
    WorkInterruptionReason? InterruptionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Gets a value indicating whether the worker is in a final state.
    /// </summary>
    public bool IsFinal => this.State.IsFinal();

    /// <summary>
    /// Gets the time the worker spent queued before beginning execution, when known.
    /// </summary>
    public TimeSpan? QueueDuration { get; init; }

    /// <summary>
    /// Gets the total retained execution duration across worker iterations.
    /// </summary>
    public TimeSpan TotalExecutionDuration { get; init; }

    /// <summary>
    /// Gets the next scheduled run time when the worker is waiting to run again.
    /// </summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>
    /// Creates a compact overview row from a fuller worker summary.
    /// </summary>
    /// <param name="worker">The worker summary to project.</param>
    /// <returns>The projected overview row.</returns>
    public static WorkerOverviewItem From(WorkerSummary worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.SubjectId,
            worker.ConcurrencyKey,
            worker.Identifiers,
            worker.Revision,
            worker.DefinitionCategory,
            worker.State,
            worker.InterruptionReason,
            worker.CreatedAt,
            worker.StateChangedAt,
            worker.UpdatedAt)
        {
            QueueDuration = worker.QueueDuration,
            TotalExecutionDuration = worker.TotalExecutionDuration,
            NextRunAt = worker.NextRunAt,
        };
}

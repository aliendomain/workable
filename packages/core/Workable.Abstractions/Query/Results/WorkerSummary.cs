namespace Workable;

/// <summary>
/// Represents a compact worker row used by summary and overview queries.
/// </summary>
/// <param name="Id">The worker identifier.</param>
/// <param name="Revision">The optimistic-concurrency revision of the worker snapshot.</param>
/// <param name="StateSequence">The monotonic state-change sequence for the worker.</param>
/// <param name="DefinitionName">The registered definition name that produced the worker.</param>
/// <param name="DefinitionCategory">The category of the registered definition.</param>
/// <param name="SubjectId">The optional primary business subject associated with the worker.</param>
/// <param name="ConcurrencyKey">The optional concurrency grouping key associated with the worker.</param>
/// <param name="Identifiers">The additional searchable identifiers associated with the worker.</param>
/// <param name="RequestContext">The caller context recorded when the worker was queued or otherwise created.</param>
/// <param name="State">The current worker lifecycle state.</param>
/// <param name="InterruptionReason">The optional interruption reason when the worker was interrupted.</param>
/// <param name="CreatedAt">The time the worker was created.</param>
/// <param name="StateChangedAt">The time the worker last changed state.</param>
/// <param name="UpdatedAt">The time the worker was last updated.</param>
public sealed record WorkerSummary(
    WorkerId Id,
    long Revision,
    long StateSequence,
    string DefinitionName,
    string DefinitionCategory,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    WorkRequestContext RequestContext,
    WorkerState State,
    WorkInterruptionReason? InterruptionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Gets the origin metadata extracted from <see cref="RequestContext"/>.
    /// </summary>
    public WorkOrigin Origin => this.RequestContext.Origin;

    /// <summary>
    /// Gets the current worker version composed from <see cref="Id"/> and <see cref="Revision"/>.
    /// </summary>
    public WorkerVersion Version => new(this.Id, this.Revision);

    /// <summary>
    /// Gets a value indicating whether the worker is in a final state.
    /// </summary>
    public bool IsFinal => this.State.IsFinal();

    /// <summary>
    /// Gets the current retry attempt number when the worker is part of a retry lineage.
    /// </summary>
    public int? RetryAttempt { get; init; }

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
    /// Gets the number of effective configuration differences relative to the current definition defaults.
    /// </summary>
    public int ConfigDifferenceCount { get; init; }
}

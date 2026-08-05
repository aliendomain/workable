namespace Workable;
/// <summary>
/// Represents the authoritative retained detail for one worker.
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
/// <param name="Input">The retained input payload, when one exists.</param>
/// <param name="Output">The retained latest output payload, when one exists.</param>
/// <param name="Options">The effective worker options used for the worker.</param>
/// <param name="Configuration">The effective work configuration used for the worker.</param>
/// <param name="Messages">The retained worker messages.</param>
/// <param name="InterruptionReason">The optional interruption reason when the worker was interrupted.</param>
/// <param name="CreatedAt">The time the worker was created.</param>
/// <param name="StateChangedAt">The time the worker last changed state.</param>
/// <param name="UpdatedAt">The time the worker was last updated.</param>
public sealed record WorkerSnapshot(
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
    WorkInput? Input,
    WorkOutput? Output,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    IReadOnlyList<WorkMessage> Messages,
    WorkInterruptionReason? InterruptionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt) : IWorkQueryResult
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
    /// Gets the retained iteration history for the worker.
    /// </summary>
    public IReadOnlyList<WorkerIterationSnapshot> Iterations { get; init; } = [];

    /// <summary>
    /// Gets the current executing iteration, when one exists.
    /// </summary>
    public WorkerIterationSnapshot? CurrentIteration { get; init; }

    /// <summary>
    /// Gets the most recently retained iteration, when one exists.
    /// </summary>
    public WorkerIterationSnapshot? LastIteration { get; init; }

    /// <summary>
    /// Gets the sequence number of the current executing iteration, when one exists.
    /// </summary>
    public long? CurrentIterationSequence { get; init; }

    /// <summary>
    /// Gets the sequence number of the most recently retained iteration, when one exists.
    /// </summary>
    public long? LastIterationSequence { get; init; }

    /// <summary>
    /// Gets the retained action and reconfiguration history for the worker.
    /// </summary>
    public IReadOnlyList<WorkerActionHistoryEntry> ActionHistory { get; init; } = [];

    /// <summary>
    /// Gets the latest retained execution profile for the worker, when profiling was enabled and the
    /// authorized session has diagnostics permission.
    /// </summary>
    public WorkProfileSnapshot? Profile { get; init; }

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
}

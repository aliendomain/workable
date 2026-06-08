namespace Workable;

/// <summary>
/// Represents the authoritative retained detail for one worker iteration.
/// </summary>
/// <param name="Sequence">The iteration sequence number within the worker.</param>
/// <param name="StartedAt">The time the iteration started executing.</param>
/// <param name="CompletedAt">The time the iteration reached its recorded status.</param>
/// <param name="ExecutionDuration">The total retained execution duration for the iteration.</param>
/// <param name="Status">The iteration completion status.</param>
/// <param name="AttemptCount">The retry attempt count within the iteration lineage.</param>
/// <param name="Output">The retained output payload, when one exists.</param>
/// <param name="Messages">The retained messages for the iteration.</param>
public sealed record WorkerIterationSnapshot(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkCompletionStatus Status,
    int AttemptCount,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages) : IWorkQueryResult
{
    /// <summary>
    /// Creates an iteration snapshot with an implicit first attempt count.
    /// </summary>
    /// <param name="Sequence">The iteration sequence number within the worker.</param>
    /// <param name="StartedAt">The time the iteration started executing.</param>
    /// <param name="CompletedAt">The time the iteration reached its recorded status.</param>
    /// <param name="ExecutionDuration">The total retained execution duration for the iteration.</param>
    /// <param name="Status">The iteration completion status.</param>
    /// <param name="Output">The retained output payload, when one exists.</param>
    /// <param name="Messages">The retained messages for the iteration.</param>
    public WorkerIterationSnapshot(
        long Sequence,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan ExecutionDuration,
        WorkCompletionStatus Status,
        WorkOutput? Output,
        IReadOnlyList<WorkMessage> Messages)
        : this(Sequence, StartedAt, CompletedAt, ExecutionDuration, Status, AttemptCount: 1, Output, Messages)
    {
    }

    /// <summary>
    /// Gets the timestamp Workable uses for occurred-at ordering in iteration result rows.
    /// </summary>
    public DateTimeOffset OccurredAt => this.CompletedAt;

    /// <summary>
    /// Gets a value indicating whether the iteration reached a final status.
    /// </summary>
    public bool IsFinal => this.Status.IsFinal();

    /// <summary>
    /// Gets the completion time when the iteration is final; otherwise <see langword="null"/>.
    /// </summary>
    public DateTimeOffset? SettledAt => this.IsFinal ? this.CompletedAt : null;

    /// <summary>
    /// Gets the execution duration when the iteration is final; otherwise <see langword="null"/>.
    /// </summary>
    public TimeSpan? SettledExecutionDuration => this.IsFinal ? this.ExecutionDuration : null;

    /// <summary>
    /// Gets the resolved failure details when the iteration represents a failure condition.
    /// </summary>
    public WorkerIterationFailure? Failure => WorkerIterationFailureResolver.Resolve(this);

    /// <summary>
    /// Gets the retained log entries for the iteration.
    /// </summary>
    public IReadOnlyList<WorkerLogEntry> Logs { get; init; } = [];

    /// <summary>
    /// Gets the retained execution profile for the iteration, when profiling was enabled.
    /// </summary>
    public WorkProfileSnapshot? Profile { get; init; }
}

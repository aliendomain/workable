namespace Workable;

public sealed record WorkerSummary(
    WorkerId Id,
    long Revision,
    long StateSequence,
    WorkDefinitionId DefinitionId,
    string DefinitionName,
    string DefinitionCategory,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    WorkOrigin Origin,
    WorkerState State,
    WorkInterruptionReason? InterruptionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt)
{
    public WorkerVersion Version => new(this.Id, this.Revision);

    public bool IsFinal => this.State.IsFinal();

    public int? RetryAttempt { get; init; }

    public TimeSpan? QueueDuration { get; init; }

    public TimeSpan TotalExecutionDuration { get; init; }

    public DateTimeOffset? NextRunAt { get; init; }

    public int ConfigDifferenceCount { get; init; }
}

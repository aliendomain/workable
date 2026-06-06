namespace Workable;
public sealed record WorkerSnapshot(
    WorkerId Id,
    long Revision,
    long StateSequence,
    WorkDefinitionId DefinitionId,
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
    public WorkOrigin Origin => this.RequestContext.Origin;

    public WorkerVersion Version => new(this.Id, this.Revision);

    public bool IsFinal => this.State.IsFinal();

    public int? RetryAttempt { get; init; }

    public IReadOnlyList<WorkerIterationSnapshot> Iterations { get; init; } = [];

    public WorkerIterationSnapshot? CurrentIteration { get; init; }

    public WorkerIterationSnapshot? LastIteration { get; init; }

    public long? CurrentIterationSequence { get; init; }

    public long? LastIterationSequence { get; init; }

    public IReadOnlyList<WorkerActionHistoryEntry> ActionHistory { get; init; } = [];

    public WorkProfileSnapshot? Profile { get; init; }

    public TimeSpan? QueueDuration { get; init; }

    public TimeSpan TotalExecutionDuration { get; init; }

    public DateTimeOffset? NextRunAt { get; init; }
}

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
    WorkOrigin Origin,
    WorkerState State,
    WorkInput? Input,
    WorkOutput? Output,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    IReadOnlyList<WorkMessage> Messages,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public WorkerVersion Version => new(this.Id, this.Revision);

    public IReadOnlyList<WorkerIterationSnapshot> Iterations { get; init; } = [];

    public IReadOnlyList<WorkerLogEntry> Logs { get; init; } = [];

    public IReadOnlyList<WorkerActionHistoryEntry> ActionHistory { get; init; } = [];

    public WorkProfileSnapshot? Profile { get; init; }
}

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
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public WorkerVersion Version => new(this.Id, this.Revision);
}

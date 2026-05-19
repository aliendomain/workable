namespace Workable;

internal sealed class WorkEventMetadata(
    WorkSystemId workSystemId,
    WorkerId? workerId,
    WorkDefinitionId? definitionId,
    WorkSubjectId? subjectId,
    WorkConcurrencyKey? concurrencyKey,
    string eventType,
    Func<IReadOnlySet<WorkIdentifier>>? getIdentifiers = null)
{
    private static readonly IReadOnlySet<WorkIdentifier> EmptyIdentifiers = new HashSet<WorkIdentifier>();
    private IReadOnlySet<WorkIdentifier>? identifiers;

    public WorkSystemId WorkSystemId { get; } = workSystemId;

    public WorkerId? WorkerId { get; } = workerId;

    public WorkDefinitionId? DefinitionId { get; } = definitionId;

    public WorkSubjectId? SubjectId { get; } = subjectId;

    public WorkConcurrencyKey? ConcurrencyKey { get; } = concurrencyKey;

    public string EventType { get; } = eventType;

    public bool ContainsIdentifier(WorkIdentifier identifier)
        => this.Identifiers.Contains(identifier);

    public bool ContainsAnyKey(IReadOnlySet<WorkEventKeyFilter>? keys)
        => WorkEventFilter.KeysMatch(keys, this.SubjectId, this.ConcurrencyKey, this.Identifiers);

    private IReadOnlySet<WorkIdentifier> Identifiers
        => this.identifiers ??= getIdentifiers?.Invoke() ?? EmptyIdentifiers;
}

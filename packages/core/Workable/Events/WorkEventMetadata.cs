namespace Workable;

internal sealed class WorkEventMetadata(
    WorkSystemId workSystemId,
    WorkerId? workerId,
    WorkDefinitionId? definitionId,
    string? definitionName,
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

    public string? DefinitionName { get; } = definitionName;

    public WorkSubjectId? SubjectId { get; } = subjectId;

    public WorkConcurrencyKey? ConcurrencyKey { get; } = concurrencyKey;

    public string EventType { get; } = eventType;

    public bool ContainsIdentifier(WorkIdentifier identifier)
        => this.Identifiers.Contains(identifier);

    public bool ContainsAnyKey(IReadOnlySet<WorkEventKeyFilter>? keys)
    {
        if (keys is not { Count: > 0 })
        {
            return true;
        }

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key.Type) || string.IsNullOrWhiteSpace(key.Value))
            {
                continue;
            }

            if (key.Kind is null or WorkKeyKind.Subject &&
                this.SubjectId is { } subject &&
                KeyEquals(key, subject.Type, subject.Value))
            {
                return true;
            }

            if (key.Kind is null or WorkKeyKind.ConcurrencyKey &&
                this.ConcurrencyKey is { } concurrencyKey &&
                KeyEquals(key, concurrencyKey.Type, concurrencyKey.Value))
            {
                return true;
            }

            if (key.Kind is null or WorkKeyKind.Identifier &&
                this.Identifiers.Contains(new WorkIdentifier(key.Type, key.Value)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool KeyEquals(WorkEventKeyFilter key, string type, string value)
        => string.Equals(key.Type, type, StringComparison.Ordinal) &&
            string.Equals(key.Value, value, StringComparison.Ordinal);

    internal IReadOnlySet<WorkIdentifier> Identifiers
        => this.identifiers ??= getIdentifiers?.Invoke() ?? EmptyIdentifiers;
}

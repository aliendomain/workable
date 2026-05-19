using System.Diagnostics.CodeAnalysis;

namespace Workable;
public sealed record WorkEventFilter(
    WorkerId? WorkerId = null,
    WorkDefinitionId? DefinitionId = null,
    IReadOnlySet<WorkDefinitionId>? DefinitionIds = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlySet<WorkEventKeyFilter>? Keys = null,
    string? EventType = null,
    IReadOnlySet<string>? EventTypes = null)
{
    public bool Matches(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        return (this.WorkerId is null || this.WorkerId == workEvent.WorkerId) &&
            (this.DefinitionId is null || this.DefinitionId == workEvent.DefinitionId) &&
            (this.DefinitionIds is not { Count: > 0 } ||
                (workEvent.DefinitionId is { } definitionId && this.DefinitionIds.Contains(definitionId))) &&
            (this.SubjectId is null || this.SubjectId == workEvent.SubjectId) &&
            (this.ConcurrencyKey is null || this.ConcurrencyKey == workEvent.ConcurrencyKey) &&
            (this.Identifier is null || workEvent.Identifiers.Contains(this.Identifier.Value)) &&
            KeysMatch(this.Keys, workEvent.SubjectId, workEvent.ConcurrencyKey, workEvent.Identifiers) &&
            EventTypeMatches(workEvent.EventType);
    }

    public bool EventTypeMatches(string eventType)
        => (this.EventType is null || string.Equals(this.EventType, eventType, StringComparison.OrdinalIgnoreCase)) &&
            (this.EventTypes is not { Count: > 0 } ||
                this.EventTypes.Contains(eventType, StringComparer.OrdinalIgnoreCase));

    internal static bool KeysMatch(
        IReadOnlySet<WorkEventKeyFilter>? keys,
        WorkSubjectId? subjectId,
        WorkConcurrencyKey? concurrencyKey,
        IReadOnlySet<WorkIdentifier> identifiers)
    {
        if (keys is not { Count: > 0 })
        {
            return true;
        }

        return keys.Any(key => key.Matches(subjectId, concurrencyKey, identifiers));
    }
}

public sealed record WorkEventKeyFilter(
    WorkKeyKind? Kind,
    string Type,
    string Value)
{
    internal bool Matches(
        WorkSubjectId? subjectId,
        WorkConcurrencyKey? concurrencyKey,
        IReadOnlySet<WorkIdentifier> identifiers)
    {
        if (string.IsNullOrWhiteSpace(this.Type) || string.IsNullOrWhiteSpace(this.Value))
        {
            return false;
        }

        return (this.Kind is null or WorkKeyKind.Subject &&
                subjectId is { } subject &&
                KeyEquals(subject.Type, subject.Value)) ||
            (this.Kind is null or WorkKeyKind.ConcurrencyKey &&
                concurrencyKey is { } key &&
                KeyEquals(key.Type, key.Value)) ||
            (this.Kind is null or WorkKeyKind.Identifier &&
                identifiers.Contains(new WorkIdentifier(this.Type, this.Value)));
    }

    private bool KeyEquals(string type, string value)
        => string.Equals(this.Type, type, StringComparison.Ordinal) &&
            string.Equals(this.Value, value, StringComparison.Ordinal);
}

using System.Diagnostics.CodeAnalysis;

namespace Workable;
/// <summary>
/// Filters work events by worker identity, definition identity, relationship keys, and event type.
/// </summary>
/// <param name="WorkerId">An optional exact worker-id filter.</param>
/// <param name="DefinitionName">An optional exact definition-name filter.</param>
/// <param name="DefinitionNames">Optional exact definition names to include.</param>
/// <param name="SubjectId">An optional exact subject-id filter.</param>
/// <param name="ConcurrencyKey">An optional exact concurrency-key filter.</param>
/// <param name="Identifier">An optional exact identifier filter.</param>
/// <param name="Keys">Optional exact key filters across subjects, concurrency keys, and identifiers.</param>
/// <param name="EventType">An optional exact event-type filter.</param>
/// <param name="EventTypes">Optional exact event types to include.</param>
public sealed record WorkEventFilter(
    WorkerId? WorkerId = null,
    string? DefinitionName = null,
    IReadOnlySet<string>? DefinitionNames = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    IReadOnlySet<WorkEventKeyFilter>? Keys = null,
    string? EventType = null,
    IReadOnlySet<string>? EventTypes = null)
{
    /// <summary>
    /// Gets the optional work or workflow definition namespace to include.
    /// </summary>
    public WorkEventDefinitionKind? DefinitionKind { get; init; }

    internal IReadOnlySet<WorkEventDefinitionScope>? AuthorizedDefinitions { get; init; }

    /// <summary>
    /// Determines whether the supplied event matches this filter.
    /// </summary>
    /// <param name="workEvent">The event to evaluate.</param>
    /// <returns><see langword="true"/> when the event matches the filter; otherwise <see langword="false"/>.</returns>
    public bool Matches(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        if (this.DefinitionKind is { } definitionKind && definitionKind != workEvent.DefinitionKind)
        {
            return false;
        }

        if (this.AuthorizedDefinitions is { Count: > 0 } authorizedDefinitions &&
            (workEvent.DefinitionScope is not { } definitionScope || !authorizedDefinitions.Contains(definitionScope)))
        {
            return false;
        }

        if (this.WorkerId is { } workerId && workerId != workEvent.WorkerId)
        {
            return false;
        }

        if (this.DefinitionName is { } definitionName &&
            !string.Equals(definitionName, workEvent.WorkDefinitionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (this.DefinitionNames is { Count: > 0 } definitionNames &&
            (string.IsNullOrWhiteSpace(workEvent.WorkDefinitionName) ||
                !definitionNames.Contains(workEvent.WorkDefinitionName, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (this.SubjectId is { } subjectId && subjectId != workEvent.SubjectId)
        {
            return false;
        }

        if (this.ConcurrencyKey is { } concurrencyKey && concurrencyKey != workEvent.ConcurrencyKey)
        {
            return false;
        }

        if (this.Identifier is { } identifier && !workEvent.Identifiers.Contains(identifier))
        {
            return false;
        }

        return KeysMatch(this.Keys, workEvent.SubjectId, workEvent.ConcurrencyKey, workEvent.Identifiers) &&
            EventTypeMatches(workEvent.EventType);
    }

    /// <summary>
    /// Determines whether the supplied event type matches the event-type portion of this filter.
    /// </summary>
    /// <param name="eventType">The event type to evaluate.</param>
    /// <returns><see langword="true"/> when the event type matches; otherwise <see langword="false"/>.</returns>
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

/// <summary>
/// Filters work events by one exact relationship key.
/// </summary>
/// <param name="Kind">The optional key kind to restrict the match to.</param>
/// <param name="Type">The exact key type to match.</param>
/// <param name="Value">The exact key value to match.</param>
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

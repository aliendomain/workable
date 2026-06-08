using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents one raw Workable event delivered over the SignalR realtime transport.
/// </summary>
/// <param name="OccurredAt">The time the underlying Workable event occurred.</param>
/// <param name="WorkSystemName">The named Workable system that produced the event, if any.</param>
/// <param name="WorkerId">The affected worker identifier, when the event is worker-specific.</param>
/// <param name="WorkDefinitionName">The work definition name associated with the event, when available.</param>
/// <param name="SubjectId">The subject id associated with the event, when available.</param>
/// <param name="ConcurrencyKey">The concurrency key associated with the event, when available.</param>
/// <param name="Identifiers">The structured identifiers attached to the event.</param>
/// <param name="EventType">The event type name.</param>
/// <param name="Data">The transport payload for the event, if the event type includes one.</param>
public sealed record WorkableRealtimeEvent(
    DateTimeOffset OccurredAt,
    string? WorkSystemName,
    WorkerId? WorkerId,
    string? WorkDefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyList<WorkIdentifier> Identifiers,
    string EventType,
    JsonElement? Data)
{
    /// <summary>
    /// Creates a realtime transport event from a core <see cref="WorkEvent"/>.
    /// </summary>
    /// <param name="workEvent">The core event to project into realtime transport form.</param>
    /// <returns>The projected realtime event.</returns>
    public static WorkableRealtimeEvent From(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        return new WorkableRealtimeEvent(
            workEvent.OccurredAt,
            workEvent.WorkSystemName,
            workEvent.WorkerId,
            workEvent.WorkDefinitionName,
            workEvent.SubjectId,
            workEvent.ConcurrencyKey,
            [.. workEvent.Identifiers],
            workEvent.EventType,
            workEvent.Data);
    }
}

/// <summary>
/// Represents a batch of raw Workable events delivered over SignalR.
/// </summary>
/// <param name="SentAt">The time the batch envelope was created for transport.</param>
/// <param name="Events">The ordered events included in the batch.</param>
public sealed record WorkableRealtimeEventBatch(
    DateTimeOffset SentAt,
    IReadOnlyList<WorkableRealtimeEvent> Events)
{
    /// <summary>
    /// Creates a realtime event batch from core Workable events.
    /// </summary>
    /// <param name="workEvents">The core events to include in the batch.</param>
    /// <returns>The projected realtime batch.</returns>
    public static WorkableRealtimeEventBatch From(IReadOnlyList<WorkEvent> workEvents)
    {
        ArgumentNullException.ThrowIfNull(workEvents);

        return new WorkableRealtimeEventBatch(
            DateTimeOffset.UtcNow,
            workEvents.Select(WorkableRealtimeEvent.From).ToArray());
    }
}

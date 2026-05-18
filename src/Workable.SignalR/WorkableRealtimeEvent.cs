using System.Text.Json;

namespace Workable;
public sealed record WorkableRealtimeEvent(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    WorkerId? WorkerId,
    WorkDefinitionId? DefinitionId,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyList<WorkIdentifier> Identifiers,
    WorkOrigin? Origin,
    string EventType,
    JsonElement? Data,
    IReadOnlyList<WorkMessage> Messages)
{
    public static WorkableRealtimeEvent From(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        return new WorkableRealtimeEvent(
            workEvent.OccurredAt,
            workEvent.WorkSystemId,
            workEvent.WorkerId,
            workEvent.DefinitionId,
            workEvent.SubjectId,
            workEvent.ConcurrencyKey,
            [.. workEvent.Identifiers],
            workEvent.Origin,
            workEvent.EventType,
            workEvent.Data,
            workEvent.Messages);
    }
}

public sealed record WorkableRealtimeEventBatch(
    DateTimeOffset SentAt,
    IReadOnlyList<WorkableRealtimeEvent> Events)
{
    public static WorkableRealtimeEventBatch From(IReadOnlyList<WorkEvent> workEvents)
    {
        ArgumentNullException.ThrowIfNull(workEvents);

        return new WorkableRealtimeEventBatch(
            DateTimeOffset.UtcNow,
            workEvents.Select(WorkableRealtimeEvent.From).ToArray());
    }
}

using System.Text.Json;

namespace Workable;
public sealed record WorkableRealtimeEvent(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId? WorkerId,
    WorkDefinitionId? WorkDefinitionId,
    string? WorkDefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyList<WorkIdentifier> Identifiers,
    string EventType,
    JsonElement? Data)
{
    public static WorkableRealtimeEvent From(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        return new WorkableRealtimeEvent(
            workEvent.OccurredAt,
            workEvent.WorkSystemId,
            workEvent.WorkSystemName,
            workEvent.WorkerId,
            workEvent.WorkDefinitionId,
            workEvent.WorkDefinitionName,
            workEvent.SubjectId,
            workEvent.ConcurrencyKey,
            [.. workEvent.Identifiers],
            workEvent.EventType,
            workEvent.Data);
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

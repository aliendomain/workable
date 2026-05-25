using System.Text.Json;

namespace Workable;
public sealed record WorkEvent
{
    public WorkEvent(
        DateTimeOffset occurredAt,
        WorkSystemId workSystemId,
        string? workSystemName,
        WorkerId? workerId,
        WorkDefinitionId? workDefinitionId,
        string? workDefinitionName,
        WorkSubjectId? subjectId,
        WorkConcurrencyKey? concurrencyKey,
        IReadOnlySet<WorkIdentifier> identifiers,
        string eventType,
        JsonElement? data)
    {
        this.OccurredAt = occurredAt;
        this.WorkSystemId = workSystemId;
        this.WorkSystemName = workSystemName;
        this.WorkerId = workerId;
        this.WorkDefinitionId = workDefinitionId;
        this.WorkDefinitionName = workDefinitionName;
        this.SubjectId = subjectId;
        this.ConcurrencyKey = concurrencyKey;
        this.Identifiers = identifiers;
        this.EventType = eventType;
        this.Data = data;
    }

    public DateTimeOffset OccurredAt { get; }

    public WorkSystemId WorkSystemId { get; }

    public string? WorkSystemName { get; }

    public WorkerId? WorkerId { get; }

    public WorkDefinitionId? WorkDefinitionId { get; }

    public string? WorkDefinitionName { get; }

    public WorkSubjectId? SubjectId { get; }

    public WorkConcurrencyKey? ConcurrencyKey { get; }

    public IReadOnlySet<WorkIdentifier> Identifiers { get; }

    public string EventType { get; }

    public JsonElement? Data { get; }

    public T? DeserializeData<T>(JsonSerializerOptions? options = null)
        => this.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } data
            ? data.Deserialize<T>(options ?? WorkEventJson.Options)
            : default;
}

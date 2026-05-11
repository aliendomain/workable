using System.Text.Json;

namespace Workable;
public sealed record WorkEvent(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    WorkerId? WorkerId,
    WorkDefinitionId? DefinitionId,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    WorkOrigin? Origin,
    string EventType,
    JsonElement? Data,
    IReadOnlyList<WorkMessage> Messages)
{
    public T? DeserializeData<T>(JsonSerializerOptions? options = null)
        => this.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } data
            ? data.Deserialize<T>(options ?? WorkEventJson.Options)
            : default;
}

using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents one ordered item in a work iteration's status stream.
/// </summary>
/// <param name="OccurredAt">The time the item was published.</param>
/// <param name="WorkSystemId">The system that owns the iteration.</param>
/// <param name="WorkSystemName">The optional configured system name.</param>
/// <param name="Iteration">The worker iteration that emitted the item.</param>
/// <param name="Sequence">The monotonic item sequence within the iteration.</param>
/// <param name="WorkDefinitionName">The work definition executing the iteration.</param>
/// <param name="Type">The application-defined status type.</param>
/// <param name="Data">The optional structured payload.</param>
public sealed record WorkIterationStatusItem(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerIterationReference Iteration,
    long Sequence,
    string WorkDefinitionName,
    string Type,
    JsonElement? Data)
{
    /// <summary>
    /// Deserializes the status payload to the requested type.
    /// </summary>
    public T? DeserializeData<T>(JsonSerializerOptions? options = null)
        => this.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } data
            ? data.Deserialize<T>(options ?? WorkEventJson.Options)
            : default;
}

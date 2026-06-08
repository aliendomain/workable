using System.Text.Json;

namespace Workable;
/// <summary>
/// Represents serialized worker output.
/// </summary>
/// <param name="Json">The serialized output payload, or <see langword="null"/> when the execution produced no output.</param>
/// <param name="ClrType">The optional CLR type name that produced the payload.</param>
/// <param name="ContentType">The content type describing the payload format.</param>
public sealed record WorkOutput(
    string? Json,
    string? ClrType = null,
    string ContentType = "application/json") : WorkData(Json, ClrType, ContentType)
{
    /// <summary>
    /// Gets a reusable empty output instance for work that produces no payload.
    /// </summary>
    public static WorkOutput Empty { get; } = new((string?)null);

    /// <summary>
    /// Creates output from an existing JSON payload.
    /// </summary>
    /// <param name="json">The serialized output payload.</param>
    /// <param name="clrType">The optional CLR type associated with the payload.</param>
    /// <returns>A work output instance containing the supplied payload.</returns>
    public static WorkOutput FromJson(string json, Type? clrType = null)
        => new(json, clrType?.AssemblyQualifiedName);

    /// <summary>
    /// Creates output by serializing a typed value.
    /// </summary>
    /// <typeparam name="T">The logical output type to serialize.</typeparam>
    /// <param name="value">The typed output value to serialize.</param>
    /// <param name="options">Optional JSON serializer options. When omitted, Workable uses its default JSON options.</param>
    /// <returns>A work output instance containing the serialized payload.</returns>
    public static WorkOutput FromValue<T>(T value, JsonSerializerOptions? options = null)
        => new(
            JsonSerializer.Serialize(value, options ?? JsonOptions),
            typeof(T).AssemblyQualifiedName);

    /// <summary>
    /// Creates output from an existing <see cref="WorkData"/> instance.
    /// </summary>
    /// <param name="data">The data instance to copy.</param>
    /// <returns>A work output instance with the same payload fields as <paramref name="data"/>.</returns>
    public static WorkOutput FromData(WorkData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new WorkOutput(data.Json, data.ClrType, data.ContentType);
    }
}

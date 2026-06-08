using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

/// <summary>
/// Base type for retained Workable data payloads such as input and output.
/// </summary>
/// <param name="Json">The serialized JSON payload, when one exists.</param>
/// <param name="ClrType">The originating CLR type name, when one is known.</param>
/// <param name="ContentType">The payload content type.</param>
public abstract record WorkData(
    string? Json,
    string? ClrType = null,
    string ContentType = "application/json")
{
    internal static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected static readonly JsonSerializerOptions JsonOptions = DefaultJsonOptions;

    /// <summary>
    /// Deserializes the retained JSON payload to a typed value.
    /// </summary>
    /// <typeparam name="T">The destination CLR type.</typeparam>
    /// <param name="options">Optional JSON serializer options to use instead of Workable's defaults.</param>
    /// <returns>The deserialized value, or the default value of <typeparamref name="T"/> when no JSON payload exists.</returns>
    public T? ToValue<T>(JsonSerializerOptions? options = null)
        => string.IsNullOrWhiteSpace(this.Json)
            ? default
            : JsonSerializer.Deserialize<T>(this.Json, options ?? JsonOptions);

    /// <summary>
    /// Deserializes the retained JSON payload to a runtime-supplied type.
    /// </summary>
    /// <param name="type">The destination CLR type.</param>
    /// <param name="options">Optional JSON serializer options to use instead of Workable's defaults.</param>
    /// <returns>The deserialized value, or <see langword="null"/> when no JSON payload exists.</returns>
    public object? ToValue(Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        return string.IsNullOrWhiteSpace(this.Json)
            ? null
            : JsonSerializer.Deserialize(this.Json, type, options ?? JsonOptions);
    }
}

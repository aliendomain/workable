using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;
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

    public T? ToValue<T>(JsonSerializerOptions? options = null)
        => string.IsNullOrWhiteSpace(this.Json)
            ? default
            : JsonSerializer.Deserialize<T>(this.Json, options ?? JsonOptions);

    public object? ToValue(Type type, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        return string.IsNullOrWhiteSpace(this.Json)
            ? null
            : JsonSerializer.Deserialize(this.Json, type, options ?? JsonOptions);
    }
}

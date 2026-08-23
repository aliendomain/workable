using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

internal static class WorkableSignalRJson
{
    public static JsonElement Serialize<T>(T value, JsonSerializerOptions options)
        => JsonSerializer.SerializeToElement(value, options);

    internal static JsonSerializerOptions CreateOptions(JsonSerializerOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        var options = new JsonSerializerOptions(hostOptions);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed class WorkableSignalRValueJsonConverter<T> : JsonConverter<T>
{
    public override T? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => JsonSerializer.Deserialize<T>(
            ref reader,
            WorkableSignalRJson.CreateOptions(options));

    public override void Write(
        Utf8JsonWriter writer,
        T value,
        JsonSerializerOptions options)
        => JsonSerializer.Serialize(
            writer,
            value,
            WorkableSignalRJson.CreateOptions(options));
}

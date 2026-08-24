using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

internal sealed class WorkableStringEnumListJsonConverter<TEnum>
    : JsonConverter<IReadOnlyList<TEnum>>
    where TEnum : struct, Enum
{
    public override bool HandleNull => true;

    public override IReadOnlyList<TEnum>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of {typeof(TEnum).Name} values.");
        }

        var values = new List<TEnum>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named))
            {
                values.Add(named);
                continue;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
            {
                values.Add((TEnum)Enum.ToObject(typeof(TEnum), numeric));
                continue;
            }

            throw new JsonException($"Invalid {typeof(TEnum).Name} value.");
        }

        return values;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<TEnum>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item.ToString());
        }

        writer.WriteEndArray();
    }
}

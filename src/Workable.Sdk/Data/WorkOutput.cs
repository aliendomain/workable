using System.Text.Json;

namespace Workable;
public sealed record WorkOutput(
    string? Json,
    string? ClrType = null,
    string ContentType = "application/json") : WorkData(Json, ClrType, ContentType)
{
    public static WorkOutput Empty { get; } = new((string?)null);

    public static WorkOutput FromJson(string json, Type? clrType = null)
        => new(json, clrType?.AssemblyQualifiedName);

    public static WorkOutput FromValue<T>(T value, JsonSerializerOptions? options = null)
        => new(
            JsonSerializer.Serialize(value, options ?? JsonOptions),
            typeof(T).AssemblyQualifiedName);

    public static WorkOutput FromData(WorkData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new WorkOutput(data.Json, data.ClrType, data.ContentType);
    }
}

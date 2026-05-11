using System.Text.Json;
using NJsonSchema.Generation;

namespace Workable;
public sealed record WorkSchema(
    string? JsonSchema,
    string ContentType = "application/schema+json")
{
    public static WorkSchema None { get; } = new((string?)null);

    public static WorkSchema FromType<T>()
        => FromType(typeof(T));

    public static WorkSchema FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var generator = new JsonSchemaGenerator(new SystemTextJsonSchemaGeneratorSettings());
        var schema = generator.Generate(type);
        return new WorkSchema(schema.ToJson());
    }
}

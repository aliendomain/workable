using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Workable;
public sealed record WorkSchema(
    string? JsonSchema,
    string ContentType = "application/schema+json",
    string? SchemaDialect = WorkSchema.JsonSchemaDialect202012)
{
    public const string JsonSchemaDialect202012 = "https://json-schema.org/draft/2020-12/schema";

    public static WorkSchema None { get; } = new((string?)null, SchemaDialect: null);

    public static WorkSchema FromType<T>()
        => FromType(typeof(T));

    public static WorkSchema FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var options = new JsonSerializerOptions(WorkData.DefaultJsonOptions)
        {
            TypeInfoResolver = WorkData.DefaultJsonOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver(),
        };
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(options, type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = static (context, node) =>
            {
                if (context.Path.IsEmpty && node is JsonObject root)
                {
                    root["$schema"] = JsonSchemaDialect202012;
                }

                return node;
            },
        });

        return new WorkSchema(schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

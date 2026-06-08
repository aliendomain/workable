using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Workable;

/// <summary>
/// Represents a structured schema document associated with work input or output.
/// </summary>
/// <param name="JsonSchema">The schema document text, when one exists.</param>
/// <param name="ContentType">The schema content type.</param>
/// <param name="SchemaDialect">The schema dialect identifier, when one exists.</param>
public sealed record WorkSchema(
    string? JsonSchema,
    string ContentType = "application/schema+json",
    string? SchemaDialect = WorkSchema.JsonSchemaDialect202012)
{
    /// <summary>
    /// The JSON Schema 2020-12 dialect identifier used by Workable-generated schemas.
    /// </summary>
    public const string JsonSchemaDialect202012 = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// Gets a schema value that indicates no schema document is available.
    /// </summary>
    public static WorkSchema None { get; } = new((string?)null, SchemaDialect: null);

    /// <summary>
    /// Generates a schema from a CLR type using Workable's JSON serialization defaults.
    /// </summary>
    /// <typeparam name="T">The CLR type from which to generate a schema.</typeparam>
    /// <returns>The generated schema.</returns>
    public static WorkSchema FromType<T>()
        => FromType(typeof(T));

    /// <summary>
    /// Generates a schema from a CLR type using Workable's JSON serialization defaults.
    /// </summary>
    /// <param name="type">The CLR type from which to generate a schema.</param>
    /// <returns>The generated schema.</returns>
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

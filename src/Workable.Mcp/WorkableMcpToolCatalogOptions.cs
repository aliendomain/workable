namespace Workable;

public sealed record WorkableMcpToolCatalogOptions
{
    public static WorkableMcpToolCatalogOptions Default { get; } = new();

    public bool IncludeDefinitionsWithoutJsonSchema { get; init; } = true;

    public string FallbackInputSchemaJson { get; init; } = """{"type":"object","additionalProperties":true}""";
}

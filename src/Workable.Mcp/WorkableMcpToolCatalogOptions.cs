namespace Workable;

/// <summary>
/// Controls how Workable definitions are projected into direct MCP work-tool descriptors.
/// </summary>
public sealed record WorkableMcpToolCatalogOptions
{
    /// <summary>
    /// Gets the default MCP tool-catalog projection options.
    /// </summary>
    public static WorkableMcpToolCatalogOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether definitions without a compatible JSON input schema should still be exposed.
    /// </summary>
    public bool IncludeDefinitionsWithoutJsonSchema { get; init; } = true;

    /// <summary>
    /// Gets the fallback JSON schema to expose when a definition does not provide a compatible JSON input schema.
    /// </summary>
    public string FallbackInputSchemaJson { get; init; } = """{"type":"object","additionalProperties":true}""";
}

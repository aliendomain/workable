namespace Workable;

/// <summary>
/// Describes one MCP-eligible work definition as exposed through the direct session API.
/// </summary>
/// <param name="Name">The original Workable work definition name.</param>
/// <param name="Description">The human-readable work description.</param>
/// <param name="Category">The definition category path.</param>
/// <param name="InputSchemaJson">The JSON schema exposed for the work input.</param>
/// <param name="InputSchemaContentType">The content type of <paramref name="InputSchemaJson"/>.</param>
/// <param name="OutputSchemaJson">The optional JSON schema exposed for the work output.</param>
/// <param name="OutputSchemaContentType">The optional content type of <paramref name="OutputSchemaJson"/>.</param>
/// <param name="UsesFallbackInputSchema">Whether the input schema came from fallback projection instead of the definition itself.</param>
/// <param name="Metadata">The definition metadata exposed alongside the descriptor.</param>
public sealed record WorkableMcpToolDescriptor(
    string Name,
    string? Description,
    string Category,
    string InputSchemaJson,
    string InputSchemaContentType,
    string? OutputSchemaJson,
    string? OutputSchemaContentType,
    bool UsesFallbackInputSchema,
    WorkDefinitionMetadata? Metadata);

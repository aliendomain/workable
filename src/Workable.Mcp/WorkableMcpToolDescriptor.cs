namespace Workable;

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

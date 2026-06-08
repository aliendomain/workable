namespace Workable;

/// <summary>
/// Represents one protocol-facing MCP tool exposed by the ASP.NET Core Workable server.
/// </summary>
/// <param name="ToolName">The protocol-safe MCP tool name.</param>
/// <param name="Description">The human-readable tool description shown to MCP clients.</param>
/// <param name="InputSchemaJson">The JSON schema exposed for tool input.</param>
/// <param name="OutputSchemaJson">The optional JSON schema exposed for tool output.</param>
/// <param name="Kind">The broad MCP tool kind.</param>
/// <param name="WorkName">The original Workable definition name when the tool represents authored work.</param>
public sealed record WorkableMcpServerToolDescriptor(
    string ToolName,
    string? Description,
    string InputSchemaJson,
    string? OutputSchemaJson,
    WorkableMcpServerToolKind Kind,
    string? WorkName = null);

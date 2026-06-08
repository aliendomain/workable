using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents the protocol-facing result returned by <see cref="WorkableMcpToolRouter"/>.
/// </summary>
/// <param name="Json">The textual JSON payload returned to the MCP client.</param>
/// <param name="StructuredContent">The optional structured payload returned alongside <paramref name="Json"/>.</param>
/// <param name="IsError">Whether the result should be treated as an MCP error response.</param>
public sealed record WorkableMcpToolResult(
    string Json,
    JsonElement? StructuredContent,
    bool IsError = false);

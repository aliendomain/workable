using System.Text.Json;

namespace Workable;

public sealed record WorkableMcpToolResult(
    string Json,
    JsonElement? StructuredContent,
    bool IsError = false);

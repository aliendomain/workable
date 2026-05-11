namespace Workable;

public sealed record WorkableMcpServerToolDescriptor(
    string ToolName,
    string? Description,
    string InputSchemaJson,
    string? OutputSchemaJson,
    WorkableMcpServerToolKind Kind,
    string? WorkName = null);

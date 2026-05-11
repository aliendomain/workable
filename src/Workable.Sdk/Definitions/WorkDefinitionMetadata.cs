namespace Workable;
public static class WorkDefinitionMetadataDefaults
{
    public const string Category = "General";
}

public sealed record WorkDefinitionMetadata(
    string Purpose,
    string? WhenToUse = null,
    string? WhenNotToUse = null,
    WorkRisk Risk = WorkRisk.Low,
    bool RequiresApproval = false,
    bool RequiresJustification = false,
    IReadOnlyList<string>? ExamplePrompts = null,
    IReadOnlyList<string>? Capabilities = null);

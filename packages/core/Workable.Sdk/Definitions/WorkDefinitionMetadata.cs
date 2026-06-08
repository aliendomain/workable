namespace Workable;
/// <summary>
/// Provides default metadata values used when a definition omits optional fields.
/// </summary>
public static class WorkDefinitionMetadataDefaults
{
    /// <summary>
    /// The fallback category used when a definition does not specify one.
    /// </summary>
    public const string Category = "General";
}

/// <summary>
/// Supplies descriptive metadata for externally consumed work definitions, especially tool- and catalog-oriented experiences.
/// </summary>
/// <param name="Purpose">A concise description of what the work is intended to accomplish.</param>
/// <param name="WhenToUse">Optional guidance describing when callers should choose this work.</param>
/// <param name="WhenNotToUse">Optional guidance describing when callers should avoid this work.</param>
/// <param name="Risk">The relative operational risk of invoking the work.</param>
/// <param name="RequiresApproval">Whether a caller should obtain approval before invoking the work.</param>
/// <param name="RequiresJustification">Whether a caller should supply a justification when invoking the work.</param>
/// <param name="ExamplePrompts">Optional example prompts or invocations that demonstrate intended usage.</param>
/// <param name="Capabilities">Optional capability tags that describe what the work can do.</param>
public sealed record WorkDefinitionMetadata(
    string Purpose,
    string? WhenToUse = null,
    string? WhenNotToUse = null,
    WorkRisk Risk = WorkRisk.Low,
    bool RequiresApproval = false,
    bool RequiresJustification = false,
    IReadOnlyList<string>? ExamplePrompts = null,
    IReadOnlyList<string>? Capabilities = null);

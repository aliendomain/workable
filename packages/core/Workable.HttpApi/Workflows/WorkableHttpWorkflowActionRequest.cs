namespace Workable;

/// <summary>
/// Describes one HTTP workflow action request.
/// </summary>
/// <param name="Description">Optional caller description recorded in the workflow action origin.</param>
public sealed record WorkableHttpWorkflowActionRequest(
    string? Description = null);

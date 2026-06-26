namespace Workable;

/// <summary>
/// Describes one HTTP workflow-start request.
/// </summary>
/// <param name="Description">Optional caller description recorded in the workflow origin.</param>
/// <param name="Completion">Whether the API should return after acceptance or after the workflow completes.</param>
public sealed record WorkableHttpWorkflowStartRequest(
    string? Description = null,
    WorkableHttpCompletion Completion = WorkableHttpCompletion.ReturnAfterAccepted);

namespace Workable;

/// <summary>
/// Represents one HTTP workflow-action result.
/// </summary>
public sealed record WorkableHttpWorkflowActionResult(
    WorkableHttpWorkflowActionStatus Status,
    WorkableHttpWorkflowActionKind Action,
    Guid RunId,
    WorkableHttpWorkflowRun? Run,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkableHttpWorkflowActionStatus.Accepted;
}

namespace Workable;

/// <summary>
/// Represents one HTTP workflow-start result.
/// </summary>
public sealed record WorkableHttpWorkflowStartResult(
    WorkableHttpWorkflowStartStatus Status,
    Guid? RunId,
    WorkableHttpWorkflowRun? Run,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkableHttpWorkflowStartStatus.Accepted;
}

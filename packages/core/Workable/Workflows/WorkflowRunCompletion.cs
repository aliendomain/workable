namespace Workable;

internal sealed record WorkflowRunCompletion(
    WorkflowRunStatus Status,
    WorkflowRunSnapshot? Run,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsCompletedSuccessfully => this.Status == WorkflowRunStatus.Completed;
}

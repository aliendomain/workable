namespace Workable;

internal sealed record WorkflowRunCompletion(
    WorkflowRunStatus Status,
    WorkflowRunSnapshot? Run,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsCompletedSuccessfully => this.Status == WorkflowRunStatus.Completed;

    public bool IsFinal => this.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;
}

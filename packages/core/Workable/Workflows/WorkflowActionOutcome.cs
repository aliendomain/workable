namespace Workable;

internal sealed record WorkflowActionOutcome(
    WorkflowActionStatus Status,
    WorkflowAction Action,
    WorkflowRunId RunId,
    WorkflowRunSnapshot? Run,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkflowActionStatus.Accepted;

    public static WorkflowActionOutcome Accepted(
        WorkflowAction action,
        WorkflowRunSnapshot run,
        IEnumerable<WorkMessage>? messages = null)
        => new(WorkflowActionStatus.Accepted, action, run.Id, run, [.. messages ?? []]);

    public static WorkflowActionOutcome NotFound(
        WorkflowAction action,
        WorkflowRunId runId)
        => new(
            WorkflowActionStatus.NotFound,
            action,
            runId,
            null,
            [WorkMessage.Error(
                "workable.workflow.run.not_found",
                $"No workflow run was found for '{runId.Value:D}'.",
                "workflow.run")]);

    public static WorkflowActionOutcome Unauthorized(
        WorkflowAction action,
        WorkflowRunId runId)
        => new(
            WorkflowActionStatus.Unauthorized,
            action,
            runId,
            null,
            [WorkMessage.Error(
                "workable.workflow.run.unauthorized",
                $"You are not authorized to operate workflow run '{runId.Value:D}'.",
                "workflow.authorization")]);

    public static WorkflowActionOutcome Invalid(
        WorkflowAction action,
        WorkflowRunId runId,
        WorkflowRunSnapshot? run,
        IEnumerable<WorkMessage> messages)
        => new(
            WorkflowActionStatus.Invalid,
            action,
            runId,
            run,
            [.. messages]);
}

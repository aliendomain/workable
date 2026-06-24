namespace Workable;

internal enum WorkflowStartStatus
{
    Accepted,
    Invalid,
    Unauthorized,
    NotFound,
}

internal sealed record WorkflowStartOutcome(
    WorkflowStartStatus Status,
    WorkflowRunId? RunId,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkflowStartStatus.Accepted;

    public static WorkflowStartOutcome Accepted(WorkflowRunId runId)
        => new(WorkflowStartStatus.Accepted, runId, []);

    public static WorkflowStartOutcome Invalid(IReadOnlyList<WorkMessage> messages)
        => new(WorkflowStartStatus.Invalid, null, messages);

    public static WorkflowStartOutcome Unauthorized(string definitionName)
        => new(
            WorkflowStartStatus.Unauthorized,
            null,
            [WorkMessage.Error(
                "workable.workflow.definition.unauthorized",
                $"You are not authorized to operate workflow '{definitionName}'.",
                "workflow.authorization")]);

    public static WorkflowStartOutcome NotFound(string definitionName)
        => new(
            WorkflowStartStatus.NotFound,
            null,
            [WorkMessage.Error(
                "workable.workflow.definition.not_found",
                $"Workflow '{definitionName}' was not found.",
                "workflow.definition")]);
}

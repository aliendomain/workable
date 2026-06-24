namespace Workable;

internal sealed record RegisteredWorkflow(
    WorkflowDefinition Definition,
    IReadOnlyList<WorkflowStepDefinition> Steps,
    WorkOperateAuthorizationConfiguration OperateAuthorization);

namespace Workable;

internal sealed record RegisteredWorkflow(
    WorkflowDefinition Definition,
    IReadOnlyList<WorkflowStepDefinition> Steps,
    WorkOperateAuthorizationConfiguration OperateAuthorization)
{
    public WorkDefinition AuthorizationDefinition { get; } = WorkDefinition.Create(
        Definition.Name,
        Definition.Description,
        Definition.Category,
        new WorkDefinitionId(Definition.Id.Value),
        Definition.InputSchema,
        Definition.OutputSchema,
        metadata: Definition.Metadata,
        authorization: Definition.Authorization);
}

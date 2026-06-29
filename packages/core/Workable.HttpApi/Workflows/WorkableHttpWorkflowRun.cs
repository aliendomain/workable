namespace Workable;

/// <summary>
/// Represents one workflow-run snapshot on the HTTP API surface.
/// </summary>
public sealed record WorkableHttpWorkflowRun(
    Guid RunId,
    string DefinitionName,
    WorkflowRunStatus Status,
    WorkflowAvailableActions AvailableActions,
    IReadOnlyList<WorkableHttpWorkflowStep> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages)
{
    internal static WorkableHttpWorkflowRun? From(WorkflowRunSnapshot? snapshot)
        => snapshot is null
            ? null
            : new WorkableHttpWorkflowRun(
                snapshot.Id.Value,
                snapshot.DefinitionName,
                snapshot.Status,
                WorkflowAvailableActions.For(snapshot.Status),
                snapshot.Steps.Select(WorkableHttpWorkflowStep.From).ToArray(),
                snapshot.CreatedAt,
                snapshot.StartedAt,
                snapshot.CompletedAt,
                snapshot.Messages);
}

/// <summary>
/// Represents one workflow-step snapshot on the HTTP API surface.
/// </summary>
public sealed record WorkableHttpWorkflowStep(
    string Name,
    WorkflowStepKind Kind,
    WorkflowStepRunStatus Status,
    IReadOnlyList<Guid> WorkerIds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages)
{
    internal static WorkableHttpWorkflowStep From(WorkflowStepRunSnapshot snapshot)
        => new(
            snapshot.Name,
            snapshot.Kind,
            snapshot.Status,
            snapshot.WorkerIds.Select(workerId => workerId.Value).ToArray(),
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.Messages);
}

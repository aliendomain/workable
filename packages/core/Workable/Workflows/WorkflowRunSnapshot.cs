namespace Workable;

internal sealed record WorkflowRunSnapshot(
    WorkflowRunId Id,
    string DefinitionName,
    WorkflowRunStatus Status,
    IReadOnlyList<WorkflowStepRunSnapshot> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages,
    IReadOnlyList<WorkflowChildReceipt> ChildReceipts);

internal sealed record WorkflowStepRunSnapshot(
    string Name,
    WorkflowStepKind Kind,
    WorkflowStepRunStatus Status,
    IReadOnlyList<WorkerId> WorkerIds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages);

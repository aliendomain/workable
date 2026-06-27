namespace Workable;

/// <summary>
/// Represents one operator-facing workflow-run list response.
/// </summary>
public sealed record WorkflowRunListView(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<WorkflowRunListItemView> Runs);

/// <summary>
/// Summarizes one workflow run for grid-style operator screens.
/// </summary>
public sealed record WorkflowRunListItemView(
    Guid RunId,
    string DefinitionName,
    WorkflowRunStatus Status,
    WorkOrigin StartedOrigin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? CurrentStepName,
    WorkflowStepKind? CurrentStepKind,
    WorkflowOperatorNodeStatus? CurrentStepStatus,
    WorkflowChildWorkerSummary OutstandingChildren,
    IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Represents one operator-facing workflow-run detail response.
/// </summary>
public sealed record WorkflowRunDetailView(
    Guid RunId,
    string DefinitionName,
    WorkflowRunStatus Status,
    WorkOrigin StartedOrigin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? CurrentStepName,
    WorkflowStepKind? CurrentStepKind,
    WorkflowOperatorNodeStatus? CurrentStepStatus,
    WorkflowChildWorkerSummary OutstandingChildren,
    IReadOnlyList<WorkflowStepOperatorView> Steps,
    IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Represents one workflow step in the operator-facing graph model.
/// </summary>
public sealed record WorkflowStepOperatorView(
    string Name,
    WorkflowStepKind Kind,
    WorkflowOperatorNodeStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    WorkflowChildWorkerSummary Children,
    IReadOnlyList<Guid> ChildWorkerIds,
    IReadOnlyList<WorkflowChildWorkerView> ChildSample,
    int AdditionalChildCount,
    IReadOnlyList<WorkflowStepOperatorView> Steps,
    IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Describes the operator-facing state of a workflow node.
/// </summary>
public enum WorkflowOperatorNodeStatus
{
    Pending,
    Running,
    WaitingOnChildren,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// Summarizes child-worker states for one workflow run or step.
/// </summary>
public sealed record WorkflowChildWorkerSummary(
    int Total,
    int Active,
    int Final,
    int Unavailable,
    IReadOnlyDictionary<WorkerState, int> ByState);

/// <summary>
/// Describes one compact child-worker sample for operator drilldown surfaces.
/// </summary>
public sealed record WorkflowChildWorkerView(
    Guid WorkerId,
    string DefinitionName,
    WorkerState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

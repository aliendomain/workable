using System.Text.Json.Serialization;

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
    [property: JsonIgnore] Guid RunId,
    [property: JsonIgnore] string DefinitionName,
    WorkflowRunStatus Status,
    WorkflowAvailableActions AvailableActions,
    [property: JsonIgnore] WorkOrigin StartedOrigin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? CurrentStepName,
    [property: JsonIgnore] WorkflowStepKind? CurrentStepKind,
    WorkflowOperatorNodeStatus? CurrentStepStatus,
    WorkflowChildWorkerSummary OutstandingChildren,
    IReadOnlyList<WorkflowStepOperatorView> Steps,
    [property: JsonIgnore] IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Represents one workflow step in the operator-facing graph model.
/// </summary>
public sealed record WorkflowStepOperatorView(
    string Name,
    WorkflowStepKind Kind,
    WorkflowOperatorNodeStatus Status,
    [property: JsonIgnore] DateTimeOffset? StartedAt,
    [property: JsonIgnore] DateTimeOffset? CompletedAt,
    WorkflowChildWorkerSummary Children,
    [property: JsonIgnore] IReadOnlyList<Guid> ChildWorkerIds,
    IReadOnlyList<WorkflowChildWorkerView> ChildSample,
    [property: JsonIgnore] int AdditionalChildCount,
    IReadOnlyList<WorkflowStepOperatorView> Steps,
    [property: JsonIgnore] IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Describes the operator-facing state of a workflow node.
/// </summary>
public enum WorkflowOperatorNodeStatus
{
    Pending,
    Running,
    WaitingOnChildren,
    Paused,
    Blocked,
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
    [property: JsonIgnore] int Unavailable,
    [property: JsonIgnore] IReadOnlyDictionary<WorkerState, int> ByState);

/// <summary>
/// Describes one compact child-worker sample for operator drilldown surfaces.
/// </summary>
public sealed record WorkflowChildWorkerView(
    Guid WorkerId,
    string DefinitionName,
    WorkerState State,
    [property: JsonIgnore] DateTimeOffset CreatedAt,
    [property: JsonIgnore] DateTimeOffset UpdatedAt);

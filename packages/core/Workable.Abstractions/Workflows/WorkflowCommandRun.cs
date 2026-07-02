namespace Workable;

/// <summary>
/// Represents the workflow run state returned by an authorized workflow command.
/// </summary>
/// <param name="RunId">The workflow run identifier.</param>
/// <param name="DefinitionName">The workflow definition name.</param>
/// <param name="Status">The current workflow run status.</param>
/// <param name="Steps">The workflow step snapshots captured with the command result.</param>
/// <param name="CreatedAt">The time when the workflow run was created.</param>
/// <param name="StartedAt">The time when the workflow run started, when it has started.</param>
/// <param name="CompletedAt">The time when the workflow run reached a final state, when it has completed.</param>
/// <param name="Messages">The messages retained by the workflow run.</param>
public sealed record WorkflowCommandRun(
    WorkflowRunId RunId,
    string DefinitionName,
    WorkflowRunStatus Status,
    IReadOnlyList<WorkflowCommandStep> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Represents one workflow step snapshot returned by an authorized workflow command.
/// </summary>
/// <param name="Name">The workflow-local step name.</param>
/// <param name="Kind">The workflow step kind.</param>
/// <param name="Status">The current step status.</param>
/// <param name="WorkerIds">The child worker ids associated with the step.</param>
/// <param name="StartedAt">The time when the step started, when it has started.</param>
/// <param name="CompletedAt">The time when the step reached a final state, when it has completed.</param>
/// <param name="Messages">The messages retained by the step.</param>
public sealed record WorkflowCommandStep(
    string Name,
    WorkflowStepKind Kind,
    WorkflowStepRunStatus Status,
    IReadOnlyList<WorkerId> WorkerIds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages);

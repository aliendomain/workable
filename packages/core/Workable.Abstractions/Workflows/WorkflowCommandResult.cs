namespace Workable;

/// <summary>
/// Represents the outcome of dispatching a workflow command.
/// </summary>
/// <param name="Status">The command status.</param>
/// <param name="RunId">The workflow run identifier associated with the command, when one exists.</param>
/// <param name="RunStatus">The latest workflow run status known to the command, when one exists.</param>
/// <param name="ErrorCode">The structured error code, when the command did not succeed.</param>
/// <param name="ErrorMessage">The human-readable error message, when the command did not succeed.</param>
/// <param name="Messages">The retained workflow messages associated with the command.</param>
public sealed record WorkflowCommandResult(
    WorkflowCommandStatus Status,
    WorkflowRunId? RunId,
    WorkflowRunStatus? RunStatus,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets a value indicating whether the command was accepted or the workflow completed successfully.
    /// </summary>
    public bool IsSuccess => this.Status is WorkflowCommandStatus.Accepted or WorkflowCommandStatus.Completed;
}

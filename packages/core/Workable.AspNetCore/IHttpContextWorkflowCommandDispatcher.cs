namespace Workable;

/// <summary>
/// Dispatches workflow commands using the current HTTP request context.
/// </summary>
public interface IHttpContextWorkflowCommandDispatcher
{
    /// <summary>
    /// Starts a workflow in the default unnamed system.
    /// </summary>
    /// <param name="workflowName">The registered workflow definition name.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="options">Optional workflow command behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the start operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Start(
        string workflowName,
        string? description = null,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a workflow in a specific system.
    /// </summary>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="workflowName">The registered workflow definition name.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="options">Optional workflow command behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the start operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> StartInSystem(
        string? systemName,
        string workflowName,
        string? description = null,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an action against a workflow run in the default unnamed system.
    /// </summary>
    /// <param name="runId">The workflow run identifier.</param>
    /// <param name="action">The workflow action to execute.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="cancellationToken">A token that cancels the action operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Execute(
        WorkflowRunId runId,
        WorkflowRunAction action,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an action against a workflow run in a specific system.
    /// </summary>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="runId">The workflow run identifier.</param>
    /// <param name="action">The workflow action to execute.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="cancellationToken">A token that cancels the action operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> ExecuteInSystem(
        string? systemName,
        WorkflowRunId runId,
        WorkflowRunAction action,
        string? description = null,
        CancellationToken cancellationToken = default);
}

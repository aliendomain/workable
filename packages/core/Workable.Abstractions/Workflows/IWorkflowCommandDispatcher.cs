namespace Workable;

/// <summary>
/// Dispatches commands against registered Workable workflow definitions and runs.
/// </summary>
public interface IWorkflowCommandDispatcher
{
    /// <summary>
    /// Starts a workflow in the default unnamed system.
    /// </summary>
    /// <param name="workflowName">The registered workflow definition name.</param>
    /// <param name="requestContext">The caller context to associate with the workflow run.</param>
    /// <param name="options">Optional workflow command behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the start operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Start(
        string workflowName,
        WorkRequestContext requestContext,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a workflow in the default unnamed system with input for steps bound to workflow input.
    /// </summary>
    /// <param name="workflowName">The registered workflow definition name.</param>
    /// <param name="requestContext">The caller context to associate with the workflow run.</param>
    /// <param name="input">Optional workflow-run input available to bound dispatch steps.</param>
    /// <param name="options">Optional workflow command behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the start operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Start(
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a workflow in a specific named system.
    /// </summary>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="workflowName">The registered workflow definition name.</param>
    /// <param name="requestContext">The caller context to associate with the workflow run.</param>
    /// <param name="options">Optional workflow command behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the start operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Start(
        string? systemName,
        string workflowName,
        WorkRequestContext requestContext,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a workflow in a specific named system with input for steps bound to workflow input.
    /// </summary>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="workflowName">The registered workflow definition name.</param>
    /// <param name="requestContext">The caller context to associate with the workflow run.</param>
    /// <param name="input">Optional workflow-run input available to bound dispatch steps.</param>
    /// <param name="options">Optional workflow command behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the start operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Start(
        string? systemName,
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an action against a workflow run in the default unnamed system.
    /// </summary>
    /// <param name="runId">The workflow run identifier.</param>
    /// <param name="action">The workflow action to execute.</param>
    /// <param name="requestContext">The caller context to associate with the workflow action.</param>
    /// <param name="cancellationToken">A token that cancels the action operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Execute(
        WorkflowRunId runId,
        WorkflowRunAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an action against a workflow run in a specific named system.
    /// </summary>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="runId">The workflow run identifier.</param>
    /// <param name="action">The workflow action to execute.</param>
    /// <param name="requestContext">The caller context to associate with the workflow action.</param>
    /// <param name="cancellationToken">A token that cancels the action operation.</param>
    /// <returns>The workflow command result.</returns>
    Task<WorkflowCommandResult> Execute(
        string? systemName,
        WorkflowRunId runId,
        WorkflowRunAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);
}

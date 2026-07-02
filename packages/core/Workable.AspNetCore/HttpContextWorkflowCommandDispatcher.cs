using Microsoft.AspNetCore.Http;

namespace Workable;

/// <summary>
/// Dispatches workflow commands using the current HTTP request context.
/// </summary>
public sealed class HttpContextWorkflowCommandDispatcher(
    IWorkflowCommandDispatcher workflows,
    IHttpContextAccessor httpContextAccessor,
    IWorkRequestContextFactory requestContexts) : IHttpContextWorkflowCommandDispatcher
{
    /// <summary>
    /// Starts a workflow in the default unnamed system using the current HTTP request context.
    /// </summary>
    public Task<WorkflowCommandResult> Start(
        string workflowName,
        string? description = null,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.StartInSystem(
            systemName: null,
            workflowName,
            description,
            options,
            cancellationToken);

    /// <summary>
    /// Starts a workflow in a specific system using the current HTTP request context.
    /// </summary>
    public Task<WorkflowCommandResult> StartInSystem(
        string? systemName,
        string workflowName,
        string? description = null,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        if (!this.TryCreateRequestContext(description, out var requestContext, out var unavailable))
        {
            return Task.FromResult(unavailable);
        }

        return workflows.Start(
            systemName,
            workflowName,
            requestContext,
            options,
            cancellationToken);
    }

    /// <summary>
    /// Executes a workflow action in the default unnamed system using the current HTTP request context.
    /// </summary>
    public Task<WorkflowCommandResult> Execute(
        WorkflowRunId runId,
        WorkflowRunAction action,
        string? description = null,
        CancellationToken cancellationToken = default)
        => this.ExecuteInSystem(
            systemName: null,
            runId,
            action,
            description,
            cancellationToken);

    /// <summary>
    /// Executes a workflow action in a specific system using the current HTTP request context.
    /// </summary>
    public Task<WorkflowCommandResult> ExecuteInSystem(
        string? systemName,
        WorkflowRunId runId,
        WorkflowRunAction action,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!this.TryCreateRequestContext(description, out var requestContext, out var unavailable))
        {
            return Task.FromResult(unavailable);
        }

        return workflows.Execute(
            systemName,
            runId,
            action,
            requestContext,
            cancellationToken);
    }

    private bool TryCreateRequestContext(
        string? description,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkRequestContext? requestContext,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out WorkflowCommandResult? unavailable)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            requestContext = null;
            unavailable = CreateRequestContextUnavailableResult();
            return false;
        }

        requestContext = requestContexts.Create(
            httpContext,
            WorkInvocationChannel.HttpApi,
            description);
        unavailable = null;
        return true;
    }

    private static WorkflowCommandResult CreateRequestContextUnavailableResult()
    {
        var messages = new[]
        {
            WorkMessage.Error(
                "workable.workflow.dispatch.http_context.unavailable",
                "The workflow command could not be completed because no current HTTP request context was available.",
                "httpContext"),
        };

        return new WorkflowCommandResult(
            WorkflowCommandStatus.RequestContextUnavailable,
            RunId: null,
            RunStatus: null,
            ErrorCode: messages[0].Code,
            ErrorMessage: messages[0].Text,
            Messages: messages);
    }
}

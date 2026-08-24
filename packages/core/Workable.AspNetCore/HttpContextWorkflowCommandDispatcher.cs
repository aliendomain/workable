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
        => this.Start(
            workflowName,
            input: null,
            description,
            options,
            cancellationToken);

    /// <summary>
    /// Starts a workflow in the default unnamed system using the current HTTP request context with workflow input.
    /// </summary>
    public Task<WorkflowCommandResult> Start(
        string workflowName,
        WorkInput? input,
        string? description = null,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.StartInSystem(
            systemName: null,
            workflowName,
            input,
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
        => this.StartInSystem(
            systemName,
            workflowName,
            input: null,
            description,
            options,
            cancellationToken);

    /// <summary>
    /// Starts a workflow in a specific system using the current HTTP request context with workflow input.
    /// </summary>
    public async Task<WorkflowCommandResult> StartInSystem(
        string? systemName,
        string workflowName,
        WorkInput? input,
        string? description = null,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        var (requestContext, unavailable) = await this.CreateRequestContext(description);
        if (requestContext is null)
        {
            return unavailable!;
        }

        return await workflows.Start(
            systemName,
            workflowName,
            requestContext,
            input,
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
    public async Task<WorkflowCommandResult> ExecuteInSystem(
        string? systemName,
        WorkflowRunId runId,
        WorkflowRunAction action,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var (requestContext, unavailable) = await this.CreateRequestContext(description);
        if (requestContext is null)
        {
            return unavailable!;
        }

        return await workflows.Execute(
            systemName,
            runId,
            action,
            requestContext,
            cancellationToken);
    }

    private async Task<(WorkRequestContext? RequestContext, WorkflowCommandResult? Unavailable)> CreateRequestContext(
        string? description)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return (null, CreateRequestContextUnavailableResult());
        }

        if (await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
        {
            await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(httpContext);
        }
        var requestContext = requestContexts.Create(
            httpContext,
            WorkInvocationChannel.HttpApi,
            description);
        return (requestContext, null);
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
            Run: null,
            ErrorCode: messages[0].Code,
            ErrorMessage: messages[0].Text,
            Messages: messages);
    }
}

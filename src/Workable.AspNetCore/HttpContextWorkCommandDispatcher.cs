using Microsoft.AspNetCore.Http;

namespace Workable;

public sealed class HttpContextWorkCommandDispatcher(
    IWorkCommandDispatcher commands,
    IHttpContextAccessor httpContextAccessor,
    IWorkRequestContextFactory requestContexts) : IHttpContextWorkCommandDispatcher
{
    public Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string workName,
        TRequest request,
        string? description = null,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Dispatch<TRequest, TResponse>(
            systemName: null,
            workName,
            request,
            description,
            options,
            cancellationToken);

    public Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string? systemName,
        string workName,
        TRequest request,
        string? description = null,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workName);

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return Task.FromResult(CreateRequestContextUnavailableResult<TResponse>());
        }

        var requestContext = requestContexts.Create(
            httpContext,
            WorkInvocationChannel.HttpApi,
            description);
        return commands.Dispatch<TRequest, TResponse>(
            systemName,
            workName,
            request,
            requestContext,
            options,
            cancellationToken);
    }

    private static WorkDispatchResult<TResponse> CreateRequestContextUnavailableResult<TResponse>()
    {
        var messages = new[]
        {
            WorkMessage.Error(
                "workable.dispatch.http_context.unavailable",
                "The command could not be completed because no current HTTP request context was available.",
                "httpContext"),
        };

        return new WorkDispatchResult<TResponse>(
            WorkDispatchStatus.RequestContextUnavailable,
            Response: default,
            WorkerId: null,
            ErrorCode: messages[0].Code,
            ErrorMessage: messages[0].Text,
            Messages: messages,
            QueueOutcome: null,
            Completion: null);
    }
}

using Microsoft.AspNetCore.Http;

namespace Workable;

/// <summary>
/// Dispatches request/response commands using the current HTTP request context.
/// </summary>
public sealed class HttpContextWorkCommandDispatcher(
    IWorkCommandDispatcher commands,
    IHttpContextAccessor httpContextAccessor,
    IWorkRequestContextFactory requestContexts) : IHttpContextWorkCommandDispatcher
{
    /// <summary>
    /// Dispatches a request to a work definition in the default unnamed system using the current HTTP request context.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="workName">The registered work definition name.</param>
    /// <param name="request">The request payload to send.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="options">Optional dispatch behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the dispatch operation.</param>
    /// <returns>The dispatch result.</returns>
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

    /// <summary>
    /// Dispatches a request to a work definition in a specific system using the current HTTP request context.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="workName">The registered work definition name.</param>
    /// <param name="request">The request payload to send.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="options">Optional dispatch behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the dispatch operation.</param>
    /// <returns>The dispatch result.</returns>
    public async Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
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
            return CreateRequestContextUnavailableResult<TResponse>();
        }

        if (await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
        {
            await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(httpContext);
        }
        var requestContext = requestContexts.Create(
            httpContext,
            WorkInvocationChannel.HttpApi,
            description);
        return await commands.Dispatch<TRequest, TResponse>(
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

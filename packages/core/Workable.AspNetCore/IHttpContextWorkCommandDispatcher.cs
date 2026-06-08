namespace Workable;

/// <summary>
/// Dispatches request/response commands using the current HTTP request context.
/// </summary>
public interface IHttpContextWorkCommandDispatcher
{
    /// <summary>
    /// Dispatches a request to a work definition in the default unnamed system.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="workName">The registered work definition name.</param>
    /// <param name="request">The request payload to send.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <param name="options">Optional dispatch behavior overrides.</param>
    /// <param name="cancellationToken">A token that cancels the dispatch operation.</param>
    /// <returns>The dispatch result.</returns>
    Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string workName,
        TRequest request,
        string? description = null,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a request to a work definition in a specific system.
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
    Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string? systemName,
        string workName,
        TRequest request,
        string? description = null,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default);
}

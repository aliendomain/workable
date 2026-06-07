namespace Workable;

public interface IHttpContextWorkCommandDispatcher
{
    Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string workName,
        TRequest request,
        string? description = null,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string? systemName,
        string workName,
        TRequest request,
        string? description = null,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default);
}

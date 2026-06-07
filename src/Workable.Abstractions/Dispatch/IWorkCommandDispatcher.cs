namespace Workable;

public interface IWorkCommandDispatcher
{
    Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string workName,
        TRequest request,
        WorkRequestContext requestContext,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string? systemName,
        string workName,
        TRequest request,
        WorkRequestContext requestContext,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default);
}

namespace Workable;

internal sealed class AuthorizedWorkQueueService(
    WorkSystemCatalog catalog,
    IWorkQueueService inner,
    WorkAuthorizationEvaluator authorization,
    WorkRequestContext requestContext) : IWorkQueueService
{
    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return NotFound(name);
        }

        var decision = authorization.AuthorizeQueue(registeredWork, input, options, requestContext);
        if (decision.IsAllowed)
        {
            return inner.Enqueue(name, input, options, cancellationToken);
        }

        return decision.IsInvalid
            ? Invalid(decision.Messages)
            : Rejected(name);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return NotFound(name);
        }

        var workInput = ToWorkInput(input);
        var decision = authorization.AuthorizeQueue(registeredWork, workInput, options, requestContext);
        if (decision.IsAllowed)
        {
            return inner.Enqueue(name, workInput, options, cancellationToken);
        }

        return decision.IsInvalid
            ? Invalid(decision.Messages)
            : Rejected(name);
    }

    private static Task<IWorkerHandle> Rejected(string name)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.Unauthorized(name)));

    private static Task<IWorkerHandle> Invalid(IReadOnlyList<WorkMessage> messages)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.Invalid(messages)));

    private static Task<IWorkerHandle> NotFound(string name)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.NotFound(name)));

    private static WorkInput? ToWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };
}

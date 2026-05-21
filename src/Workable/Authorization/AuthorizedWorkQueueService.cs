namespace Workable;

internal sealed class AuthorizedWorkQueueService(
    IWorkCatalog catalog,
    IWorkQueueService inner,
    WorkAuthorizationEvaluator authorization) : IWorkQueueService
{
    public Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(definitionId, out var definition))
        {
            return NotFound(definitionId);
        }

        return authorization.CanOperate(definition)
            ? inner.Enqueue(definitionId, input, options, cancellationToken)
            : Rejected(definitionId);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(definitionId, out var definition))
        {
            return NotFound(definitionId);
        }

        return authorization.CanOperate(definition)
            ? inner.Enqueue(definitionId, input, options, cancellationToken)
            : Rejected(definitionId);
    }

    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(name, out var definition))
        {
            return NotFound(name);
        }

        return authorization.CanOperate(definition)
            ? inner.Enqueue(name, input, options, cancellationToken)
            : Rejected(name);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(name, out var definition))
        {
            return NotFound(name);
        }

        return authorization.CanOperate(definition)
            ? inner.Enqueue(name, input, options, cancellationToken)
            : Rejected(name);
    }

    private static Task<IWorkerHandle> Rejected(WorkDefinitionId definitionId)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.Unauthorized(definitionId.ToString(), definitionId)));

    private static Task<IWorkerHandle> Rejected(string name)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.Unauthorized(name)));

    private static Task<IWorkerHandle> NotFound(WorkDefinitionId definitionId)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.NotFound(definitionId.ToString())));

    private static Task<IWorkerHandle> NotFound(string name)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.NotFound(name)));
}

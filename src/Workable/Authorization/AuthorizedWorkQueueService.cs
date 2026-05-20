namespace Workable;

internal sealed class AuthorizedWorkQueueService(
    IWorkCatalog catalog,
    IWorkQueueService inner,
    WorkAuthorizationScope scope) : IWorkQueueService
{
    public Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => scope.CanOperate(definitionId)
            ? inner.Enqueue(definitionId, input, options, cancellationToken)
            : Rejected(definitionId);

    public Task<IWorkerHandle> Enqueue<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => scope.CanOperate(definitionId)
            ? inner.Enqueue(definitionId, input, options, cancellationToken)
            : Rejected(definitionId);

    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.CanOperate(name)
            ? inner.Enqueue(name, input, options, cancellationToken)
            : Rejected(name);

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.CanOperate(name)
            ? inner.Enqueue(name, input, options, cancellationToken)
            : Rejected(name);

    private bool CanOperate(string name)
        => catalog.TryGet(name, out var definition) && scope.CanOperate(definition.Id);

    private static Task<IWorkerHandle> Rejected(WorkDefinitionId definitionId)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.NotFound(definitionId.ToString())));

    private static Task<IWorkerHandle> Rejected(string name)
        => Task.FromResult<IWorkerHandle>(WorkerHandle.Rejected(WorkQueueOutcome.NotFound(name)));
}

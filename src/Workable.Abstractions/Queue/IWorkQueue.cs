namespace Workable;
public interface IWorkQueue
{
    Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IWorkerHandle> Enqueue<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);
}

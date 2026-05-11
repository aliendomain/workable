namespace Workable;

internal interface IOriginAwareWorkSystem
{
    Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input,
        WorkerOptions? options,
        WorkOrigin origin,
        CancellationToken cancellationToken);

    Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input,
        WorkerOptions? options,
        WorkOrigin origin,
        CancellationToken cancellationToken);

    Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        WorkOrigin origin,
        CancellationToken cancellationToken);

    Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        WorkOrigin origin,
        CancellationToken cancellationToken);
}

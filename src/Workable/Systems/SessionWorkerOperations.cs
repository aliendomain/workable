namespace Workable;

internal sealed class SessionWorkerOperations(
    WorkerOperations inner,
    WorkRequestContext requestContext) : IWorkerOperations
{
    public Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default)
        => inner.Execute(worker, action, requestContext, cancellationToken);

    public Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
        => inner.ExecuteAll(action, filter, requestContext, cancellationToken);

    public Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default)
        => inner.Reconfigure(worker, changes, requestContext, cancellationToken);
}

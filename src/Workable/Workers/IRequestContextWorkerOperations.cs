namespace Workable;

internal interface IRequestContextWorkerOperations
{
    Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken);

    Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken);

    Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken);
}

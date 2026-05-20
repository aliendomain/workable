namespace Workable;

internal interface IWorkSystemShutdownInspection
{
    Task<WorkerQueryResult> Workers(
        WorkerCriteria criteria,
        CancellationToken cancellationToken = default);
}

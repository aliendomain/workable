namespace Workable;

public sealed class WorkableHttpWorkerAdapter
{
    public Task<WorkActionOutcome> Execute(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return session.Workers.Execute(new WorkerVersion(workerId, request.Revision), action, cancellationToken);
    }

    public Task<WorkerBulkActionOutcome> ExecuteAll(
        IWorkSystemSession session,
        WorkAction action,
        WorkableHttpWorkerBulkActionRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.Workers.ExecuteAll(
            action,
            request?.ToFilter(),
            cancellationToken);
    }

    public Task<WorkActionOutcome> Reconfigure(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return session.Workers.Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, cancellationToken);
    }

}

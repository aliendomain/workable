namespace Workable;

public sealed class WorkableHttpWorkerAdapter
{
    public Task<WorkActionOutcome> Execute(
        IWorkSystem system,
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return system.Workers.Execute(new WorkerVersion(workerId, request.Revision), action, cancellationToken);
    }

    internal Task<WorkActionOutcome> Execute(
        IWorkSystem system,
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return WorkableHttpOriginAwareSystem.Required(system).Execute(new WorkerVersion(workerId, request.Revision), action, origin, cancellationToken);
    }

    internal static Task<WorkActionOutcome> ExecuteCore(
        IWorkSystem system,
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return WorkableHttpOriginAwareSystem.Required(system).Execute(new WorkerVersion(workerId, request.Revision), action, origin, cancellationToken);
    }

    internal static Task<WorkerBulkActionOutcome> ExecuteAllCore(
        IWorkSystem system,
        WorkAction action,
        WorkableHttpWorkerBulkActionRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(origin);

        return WorkableHttpOriginAwareSystem.Required(system).ExecuteAll(
            action,
            request?.ToFilter(),
            origin,
            cancellationToken);
    }

    public Task<WorkActionOutcome> Reconfigure(
        IWorkSystem system,
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return system.Workers.Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, cancellationToken);
    }

    internal Task<WorkActionOutcome> Reconfigure(
        IWorkSystem system,
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return WorkableHttpOriginAwareSystem.Required(system).Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, origin, cancellationToken);
    }

    internal static Task<WorkActionOutcome> ReconfigureCore(
        IWorkSystem system,
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return WorkableHttpOriginAwareSystem.Required(system).Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, origin, cancellationToken);
    }
}

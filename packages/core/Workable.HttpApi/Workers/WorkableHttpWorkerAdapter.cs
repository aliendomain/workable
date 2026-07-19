namespace Workable;

/// <summary>
/// Adapts worker actions and reconfiguration requests to the HTTP API surface.
/// </summary>
public sealed class WorkableHttpWorkerAdapter
{
    /// <summary>
    /// Executes a single-worker action using the HTTP request contract.
    /// </summary>
    /// <param name="session">The authorized session that owns the target worker.</param>
    /// <param name="workerId">The target worker identifier.</param>
    /// <param name="action">The worker action to execute.</param>
    /// <param name="request">The HTTP action request containing the expected revision.</param>
    /// <param name="cancellationToken">A token that cancels the action request.</param>
    /// <returns>The worker action outcome.</returns>
    public Task<WorkActionOutcome> Execute(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return session.Workers.Execute(
            new WorkerVersion(workerId, request.Revision),
            new WorkerActionRequest(action, request.Description),
            cancellationToken);
    }

    /// <summary>
    /// Executes a bulk worker action using the HTTP request contract.
    /// </summary>
    /// <param name="session">The authorized session whose visible workers may be matched.</param>
    /// <param name="action">The worker action to execute for each matched worker.</param>
    /// <param name="request">The optional bulk-action filter payload.</param>
    /// <param name="cancellationToken">A token that cancels the bulk action request.</param>
    /// <returns>The bulk worker action outcome.</returns>
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

    /// <summary>
    /// Reconfigures one worker using the HTTP request contract.
    /// </summary>
    /// <param name="session">The authorized session that owns the target worker.</param>
    /// <param name="workerId">The target worker identifier.</param>
    /// <param name="request">The HTTP reconfiguration request containing the expected revision and changes.</param>
    /// <param name="cancellationToken">A token that cancels the reconfiguration request.</param>
    /// <returns>The worker action outcome that reports the reconfiguration result.</returns>
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

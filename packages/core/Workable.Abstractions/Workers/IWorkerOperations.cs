using System.Diagnostics.CodeAnalysis;

namespace Workable;
/// <summary>
/// Executes worker actions and worker-level reconfiguration requests.
/// </summary>
public interface IWorkerOperations
{
    /// <summary>
    /// Applies one worker action to one worker version.
    /// </summary>
    /// <param name="worker">The worker version the action should apply to.</param>
    /// <param name="action">The action to perform.</param>
    /// <param name="cancellationToken">A token that cancels the request before it completes.</param>
    /// <returns>A task that returns the action outcome.</returns>
    Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one worker action request to one worker version.
    /// </summary>
    /// <param name="worker">The worker version the action should apply to.</param>
    /// <param name="request">The action and optional reason to apply.</param>
    /// <param name="cancellationToken">A token that cancels the request before it completes.</param>
    /// <returns>A task that returns the action outcome.</returns>
    Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.Execute(worker, request.Action, cancellationToken);
    }

    /// <summary>
    /// Applies one worker action to every worker matched by the supplied bulk-action filter.
    /// </summary>
    /// <param name="action">The action to perform.</param>
    /// <param name="filter">An optional filter that restricts which workers receive the action.</param>
    /// <param name="cancellationToken">A token that cancels the request before it completes.</param>
    /// <returns>A task that returns the aggregate outcome for the bulk action.</returns>
    Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies worker-level reconfiguration changes to one worker version.
    /// </summary>
    /// <param name="worker">The worker version the reconfiguration should apply to.</param>
    /// <param name="changes">The worker-level configuration changes to apply.</param>
    /// <param name="cancellationToken">A token that cancels the request before it completes.</param>
    /// <returns>A task that returns the action outcome for the reconfiguration request.</returns>
    Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default);
}

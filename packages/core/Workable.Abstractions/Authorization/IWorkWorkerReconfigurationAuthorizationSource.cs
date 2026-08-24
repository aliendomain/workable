namespace Workable;

/// <summary>
/// Exposes advisory, caller-scoped worker reconfiguration authorization for view projections.
/// </summary>
/// <remarks>
/// This capability is for rendering permission-aware controls. The worker operation must still
/// perform its normal authoritative authorization check when the change is submitted.
/// </remarks>
public interface IWorkWorkerReconfigurationAuthorizationSource
{
    /// <summary>
    /// Determines whether the current session may request the specified worker reconfiguration.
    /// </summary>
    bool CanReconfigureWorker(WorkerSnapshot worker, WorkerReconfiguration changes);
}

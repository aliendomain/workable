namespace Workable;

/// <summary>
/// Represents the lifecycle state of a worker.
/// </summary>
public enum WorkerState
{
    /// <summary>
    /// The worker has been accepted and is waiting to run.
    /// </summary>
    Queued,

    /// <summary>
    /// The worker is actively executing.
    /// </summary>
    Running,

    /// <summary>
    /// The worker is waiting for a future scheduled run.
    /// </summary>
    Waiting,

    /// <summary>
    /// The worker is waiting to retry after a failed attempt.
    /// </summary>
    Retrying,

    /// <summary>
    /// The worker is transitioning into a paused state.
    /// </summary>
    Pausing,

    /// <summary>
    /// The worker is paused and will not execute until resumed.
    /// </summary>
    Paused,

    /// <summary>
    /// The worker is being interrupted.
    /// </summary>
    Interrupting,

    /// <summary>
    /// The worker was interrupted before reaching a normal terminal outcome.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The worker is being canceled.
    /// </summary>
    Canceling,

    /// <summary>
    /// The worker was canceled before completing successfully.
    /// </summary>
    Canceled,

    /// <summary>
    /// The worker completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The worker completed in a failed state.
    /// </summary>
    Failed,
}

namespace Workable;
/// <summary>
/// Describes the current or final completion state of a worker.
/// </summary>
public enum WorkCompletionStatus
{
    /// <summary>
    /// The worker is still executing and has not yet reached a final state.
    /// </summary>
    Executing,
    /// <summary>
    /// The worker completed successfully.
    /// </summary>
    Completed,
    /// <summary>
    /// The worker reached a failure state.
    /// </summary>
    Failed,
    /// <summary>
    /// The worker is paused and may resume later.
    /// </summary>
    Paused,
    /// <summary>
    /// The worker stopped because execution was interrupted before normal completion.
    /// </summary>
    Interrupted,
    /// <summary>
    /// The worker was canceled.
    /// </summary>
    Canceled,
    /// <summary>
    /// The worker could not be queued or completed because the request was invalid.
    /// </summary>
    Invalid,
    /// <summary>
    /// The requested worker could not be found.
    /// </summary>
    NotFound,
}

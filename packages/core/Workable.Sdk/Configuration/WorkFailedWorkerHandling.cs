namespace Workable;

/// <summary>
/// Describes how Workable should treat a worker that settles into the <c>Failed</c> state.
/// </summary>
public enum WorkFailedWorkerHandling
{
    /// <summary>
    /// Leaves failed workers in <c>Failed</c> until an operator starts or cancels them.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Automatically cancels failed workers after the configured failed-state delay elapses.
    /// </summary>
    AutoCancel = 1,
}

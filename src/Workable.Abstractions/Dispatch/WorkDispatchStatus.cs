namespace Workable;

/// <summary>
/// Represents the outcome state of a command dispatch.
/// </summary>
public enum WorkDispatchStatus
{
    /// <summary>
    /// The request was accepted for execution but dispatch returned before completion.
    /// </summary>
    Accepted,

    /// <summary>
    /// The request is currently executing and has not yet reached a terminal outcome.
    /// </summary>
    Executing,

    /// <summary>
    /// The request completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The request completed in a failed state.
    /// </summary>
    Failed,

    /// <summary>
    /// The request is paused and not currently executing.
    /// </summary>
    Paused,

    /// <summary>
    /// The request was interrupted before normal completion.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The request was canceled before normal completion.
    /// </summary>
    Canceled,

    /// <summary>
    /// The request payload or dispatch parameters were invalid.
    /// </summary>
    Invalid,

    /// <summary>
    /// The requested work or worker could not be found.
    /// </summary>
    NotFound,

    /// <summary>
    /// The caller was not authorized to dispatch the requested work.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The requested system name could not be resolved.
    /// </summary>
    SystemNotFound,

    /// <summary>
    /// Dispatch required caller context that was unavailable.
    /// </summary>
    RequestContextUnavailable,
}

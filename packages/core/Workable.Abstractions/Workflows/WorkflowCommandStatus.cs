namespace Workable;

/// <summary>
/// Represents the outcome state of a workflow command.
/// </summary>
public enum WorkflowCommandStatus
{
    /// <summary>
    /// The workflow command was accepted but returned before final completion.
    /// </summary>
    Accepted,

    /// <summary>
    /// The workflow run is currently executing and has not yet reached a terminal outcome.
    /// </summary>
    Running,

    /// <summary>
    /// The workflow run is paused and not currently executing.
    /// </summary>
    Paused,

    /// <summary>
    /// The workflow run is blocked on one or more unsuccessful child workers.
    /// </summary>
    Blocked,

    /// <summary>
    /// The workflow run completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The workflow run completed in a failed state.
    /// </summary>
    Failed,

    /// <summary>
    /// The workflow run was canceled before normal completion.
    /// </summary>
    Canceled,

    /// <summary>
    /// The command payload or command parameters were invalid.
    /// </summary>
    Invalid,

    /// <summary>
    /// The requested workflow definition or workflow run could not be found.
    /// </summary>
    NotFound,

    /// <summary>
    /// The caller was not authorized to operate the requested workflow definition or run.
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

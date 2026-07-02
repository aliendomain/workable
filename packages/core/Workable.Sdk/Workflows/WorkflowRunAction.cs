namespace Workable;

/// <summary>
/// Describes a control action that can be executed against a workflow run.
/// </summary>
public enum WorkflowRunAction
{
    /// <summary>
    /// Starts or resumes a paused or blocked workflow run.
    /// </summary>
    Start,

    /// <summary>
    /// Pauses a running workflow run and pauses outstanding child workers when possible.
    /// </summary>
    Pause,

    /// <summary>
    /// Cancels a running, paused, or blocked workflow run.
    /// </summary>
    Cancel,
}

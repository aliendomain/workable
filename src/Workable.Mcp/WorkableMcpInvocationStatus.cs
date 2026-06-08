namespace Workable;

/// <summary>
/// Represents the outcome state of a direct MCP invocation.
/// </summary>
public enum WorkableMcpInvocationStatus
{
    /// <summary>
    /// The work request was rejected before it was accepted for execution.
    /// </summary>
    Rejected,

    /// <summary>
    /// The work request was accepted and invocation returned before completion.
    /// </summary>
    Accepted,

    /// <summary>
    /// The work completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The work completed in a failed state.
    /// </summary>
    Failed,

    /// <summary>
    /// The work was interrupted before normal completion.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The work was canceled before normal completion.
    /// </summary>
    Canceled,
}

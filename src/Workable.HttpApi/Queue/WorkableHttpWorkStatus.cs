namespace Workable;

/// <summary>
/// Represents the high-level HTTP queue outcome state.
/// </summary>
public enum WorkableHttpWorkStatus
{
    /// <summary>
    /// The queue request was rejected before the worker was accepted.
    /// </summary>
    Rejected,

    /// <summary>
    /// The worker was accepted and the HTTP call returned before completion.
    /// </summary>
    Accepted,

    /// <summary>
    /// The worker completed successfully before the HTTP call returned.
    /// </summary>
    Completed,

    /// <summary>
    /// The worker completed in a failed state before the HTTP call returned.
    /// </summary>
    Failed,

    /// <summary>
    /// The worker was interrupted before normal completion.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The worker was canceled before normal completion.
    /// </summary>
    Canceled,
}

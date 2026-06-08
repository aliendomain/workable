namespace Workable;
/// <summary>
/// Describes the immediate result of a worker action request.
/// </summary>
public enum WorkActionStatus
{
    /// <summary>
    /// The action request was accepted and applied.
    /// </summary>
    Accepted,
    /// <summary>
    /// The action request was rejected because it was invalid for the worker's current state or inputs.
    /// </summary>
    Invalid,
    /// <summary>
    /// The action request conflicted with the supplied worker version or current worker state.
    /// </summary>
    Conflict,
    /// <summary>
    /// The caller was not authorized to perform the action.
    /// </summary>
    Unauthorized,
    /// <summary>
    /// No matching worker was found.
    /// </summary>
    NotFound,
}

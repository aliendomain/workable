namespace Workable;
/// <summary>
/// Describes the immediate result of a queue request.
/// </summary>
public enum WorkQueueStatus
{
    /// <summary>
    /// The request was accepted and a worker was created.
    /// </summary>
    Accepted,
    /// <summary>
    /// The request was rejected because it was invalid for the target definition or current system state.
    /// </summary>
    Invalid,
    /// <summary>
    /// The caller was not authorized to queue the target definition.
    /// </summary>
    Unauthorized,
    /// <summary>
    /// No matching definition was found for the target name.
    /// </summary>
    NotFound,
}

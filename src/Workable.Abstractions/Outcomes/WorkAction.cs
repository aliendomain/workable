namespace Workable;
/// <summary>
/// Identifies a worker action that can be requested through worker operations.
/// </summary>
public enum WorkAction
{
    /// <summary>
    /// Starts a worker that is allowed to start from its current state.
    /// </summary>
    Start,
    /// <summary>
    /// Pauses a running or startable worker.
    /// </summary>
    Pause,
    /// <summary>
    /// Cancels the worker.
    /// </summary>
    Cancel,
    /// <summary>
    /// Pushes a waiting worker to run again immediately when its configuration allows it.
    /// </summary>
    Push,
    /// <summary>
    /// Permanently purges a worker and its retained data.
    /// </summary>
    Purge,
}

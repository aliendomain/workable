namespace Workable;

/// <summary>
/// Identifies what kind of history entry was recorded for a worker.
/// </summary>
public enum WorkerActionHistoryKind
{
    /// <summary>
    /// The history entry represents an explicit worker action request.
    /// </summary>
    WorkerAction,

    /// <summary>
    /// The history entry represents a worker reconfiguration request.
    /// </summary>
    Reconfiguration,
}

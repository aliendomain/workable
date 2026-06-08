namespace Workable;

/// <summary>
/// Selects where enabled coordination features store their shared state.
/// </summary>
public enum WorkCoordinationStorage
{
    /// <summary>
    /// Uses in-memory coordination inside the current Workable runtime only.
    /// </summary>
    Local,

    /// <summary>
    /// Uses the configured persistence integration so multiple runtimes can coordinate through shared storage.
    /// </summary>
    Persistent,
}

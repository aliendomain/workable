namespace Workable;

/// <summary>
/// Represents the host lifecycle state of a work system.
/// </summary>
public enum WorkSystemState
{
    /// <summary>
    /// The system has been created but not yet started.
    /// </summary>
    Created,

    /// <summary>
    /// The system is starting its runtime components.
    /// </summary>
    Starting,

    /// <summary>
    /// The system is started and available for use.
    /// </summary>
    Started,

    /// <summary>
    /// The system is stopping its runtime components.
    /// </summary>
    Stopping,

    /// <summary>
    /// The system has fully stopped.
    /// </summary>
    Stopped,
}

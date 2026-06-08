namespace Workable;

/// <summary>
/// Represents why Workable interrupted a running or scheduled worker.
/// </summary>
public enum WorkInterruptionReason
{
    /// <summary>
    /// The owning system is shutting down.
    /// </summary>
    Shutdown,

    /// <summary>
    /// The worker lost durable lease ownership and must stop.
    /// </summary>
    LeaseLost,
}

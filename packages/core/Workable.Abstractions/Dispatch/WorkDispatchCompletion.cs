namespace Workable;

/// <summary>
/// Controls how long a command dispatch waits before returning.
/// </summary>
public enum WorkDispatchCompletion
{
    /// <summary>
    /// Return as soon as the work is accepted for execution.
    /// </summary>
    ReturnAfterAccepted,

    /// <summary>
    /// Wait until the work reaches a terminal completion outcome.
    /// </summary>
    WaitForCompletion,
}

namespace Workable;

/// <summary>
/// Controls how long an HTTP queue request waits before returning.
/// </summary>
public enum WorkableHttpCompletion
{
    /// <summary>
    /// Return as soon as the worker is accepted for execution.
    /// </summary>
    ReturnAfterAccepted,

    /// <summary>
    /// Wait until the queued worker reaches a terminal completion outcome.
    /// </summary>
    WaitForCompletion,
}

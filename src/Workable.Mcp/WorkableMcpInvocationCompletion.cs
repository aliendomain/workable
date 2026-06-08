namespace Workable;

/// <summary>
/// Controls how long MCP invocation waits before returning.
/// </summary>
public enum WorkableMcpInvocationCompletion
{
    /// <summary>
    /// Return as soon as the work is accepted for execution.
    /// </summary>
    ReturnAfterAccepted,

    /// <summary>
    /// Wait until the queued work reaches a terminal completion outcome.
    /// </summary>
    WaitForCompletion,
}

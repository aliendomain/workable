namespace Workable;

/// <summary>
/// Controls how direct MCP invocation behaves when queueing work through a session.
/// </summary>
public sealed record WorkableMcpInvocationOptions
{
    /// <summary>
    /// Gets the default MCP invocation options.
    /// </summary>
    public static WorkableMcpInvocationOptions Default { get; } = new();

    /// <summary>
    /// Gets the completion behavior used by the invocation.
    /// </summary>
    public WorkableMcpInvocationCompletion Completion { get; init; } = WorkableMcpInvocationCompletion.WaitForCompletion;

    /// <summary>
    /// Gets optional queue-time worker option overrides to apply to the queued worker.
    /// </summary>
    public WorkerOptions? WorkerOptions { get; init; }

    /// <summary>
    /// Gets an optional maximum time to wait for completion when <see cref="Completion"/> is <see cref="WorkableMcpInvocationCompletion.WaitForCompletion"/>.
    /// </summary>
    public TimeSpan? CompletionTimeout { get; init; }
}

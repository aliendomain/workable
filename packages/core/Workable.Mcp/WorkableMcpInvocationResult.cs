namespace Workable;

/// <summary>
/// Represents the outcome of queuing or completing work through the direct MCP invocation API.
/// </summary>
/// <param name="Status">The MCP-oriented invocation status.</param>
/// <param name="QueueOutcome">The immediate queue outcome returned by Workable.</param>
/// <param name="WorkerId">The queued worker identifier, when one exists.</param>
/// <param name="Completion">The terminal completion outcome, when invocation waited for completion.</param>
/// <param name="Output">The final output payload, when one was produced.</param>
/// <param name="Messages">The retained messages associated with the queue or completion outcome.</param>
public sealed record WorkableMcpInvocationResult(
    WorkableMcpInvocationStatus Status,
    WorkQueueOutcome QueueOutcome,
    WorkerId? WorkerId,
    WorkCompletion? Completion,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets a value indicating whether the invocation completed successfully.
    /// </summary>
    public bool IsCompletedSuccessfully => this.Status == WorkableMcpInvocationStatus.Completed;
}

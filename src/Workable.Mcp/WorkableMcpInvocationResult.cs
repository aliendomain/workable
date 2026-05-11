namespace Workable;

public sealed record WorkableMcpInvocationResult(
    WorkableMcpInvocationStatus Status,
    WorkQueueOutcome QueueOutcome,
    WorkerId? WorkerId,
    WorkCompletion? Completion,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsCompletedSuccessfully => this.Status == WorkableMcpInvocationStatus.Completed;
}

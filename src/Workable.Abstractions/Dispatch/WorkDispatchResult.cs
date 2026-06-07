namespace Workable;

public sealed record WorkDispatchResult<TResponse>(
    WorkDispatchStatus Status,
    TResponse? Response,
    WorkerId? WorkerId,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<WorkMessage> Messages,
    WorkQueueOutcome? QueueOutcome = null,
    WorkCompletion<TResponse>? Completion = null)
{
    public bool IsSuccess => this.Status is WorkDispatchStatus.Accepted or WorkDispatchStatus.Completed;
}

namespace Workable;

/// <summary>
/// Represents the outcome of dispatching a request/response style command.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <param name="Status">The dispatch status.</param>
/// <param name="Response">The typed response payload, when one was produced.</param>
/// <param name="WorkerId">The worker identifier associated with the dispatch, when one exists.</param>
/// <param name="ErrorCode">The structured error code, when the dispatch did not succeed.</param>
/// <param name="ErrorMessage">The human-readable error message, when the dispatch did not succeed.</param>
/// <param name="Messages">The retained work messages associated with the dispatch.</param>
/// <param name="QueueOutcome">The immediate queue outcome, when dispatch reached the queueing stage.</param>
/// <param name="Completion">The terminal completion outcome, when dispatch waited for completion.</param>
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
    /// <summary>
    /// Gets a value indicating whether dispatch succeeded through acceptance or completion.
    /// </summary>
    public bool IsSuccess => this.Status is WorkDispatchStatus.Accepted or WorkDispatchStatus.Completed;
}

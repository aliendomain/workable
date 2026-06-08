namespace Workable;

/// <summary>
/// Represents the HTTP response payload returned after queueing work.
/// </summary>
/// <param name="Status">The HTTP queue status.</param>
/// <param name="QueueOutcome">The immediate queue outcome returned by Workable.</param>
/// <param name="WorkerId">The queued worker identifier, when one exists.</param>
/// <param name="Completion">The terminal completion outcome, when the request waited for completion.</param>
/// <param name="Output">The final output payload, when one was produced.</param>
/// <param name="Messages">The retained messages associated with the queue or completion outcome.</param>
public sealed record WorkableHttpWorkResult(
    WorkableHttpWorkStatus Status,
    WorkQueueOutcome QueueOutcome,
    WorkerId? WorkerId,
    WorkCompletion? Completion,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages);

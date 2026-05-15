namespace Workable;

public sealed record WorkableHttpWorkResult(
    WorkableHttpWorkStatus Status,
    WorkQueueOutcome QueueOutcome,
    WorkerId? WorkerId,
    WorkCompletion? Completion,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages);

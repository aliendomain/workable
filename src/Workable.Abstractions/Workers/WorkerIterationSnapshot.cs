namespace Workable;

public sealed record WorkerIterationSnapshot(
    long Sequence,
    DateTimeOffset OccurredAt,
    WorkCompletionStatus Status,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    public WorkProfileSnapshot? Profile { get; init; }
}

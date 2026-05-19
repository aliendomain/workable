namespace Workable;

public sealed record WorkerIterationSnapshot(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkCompletionStatus Status,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages) : IWorkQueryResult
{
    public DateTimeOffset OccurredAt => this.CompletedAt;

    public IReadOnlyList<WorkerLogEntry> Logs { get; init; } = [];

    public WorkProfileSnapshot? Profile { get; init; }
}

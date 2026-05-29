namespace Workable;

public sealed record WorkerIterationSnapshot(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkCompletionStatus Status,
    int AttemptCount,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages) : IWorkQueryResult
{
    public WorkerIterationSnapshot(
        long Sequence,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan ExecutionDuration,
        WorkCompletionStatus Status,
        WorkOutput? Output,
        IReadOnlyList<WorkMessage> Messages)
        : this(Sequence, StartedAt, CompletedAt, ExecutionDuration, Status, AttemptCount: 1, Output, Messages)
    {
    }

    public DateTimeOffset OccurredAt => this.CompletedAt;

    public bool IsFinal => this.Status.IsFinal();

    public DateTimeOffset? SettledAt => this.IsFinal ? this.CompletedAt : null;

    public TimeSpan? SettledExecutionDuration => this.IsFinal ? this.ExecutionDuration : null;

    public WorkerIterationFailure? Failure => WorkerIterationFailureResolver.Resolve(this);

    public IReadOnlyList<WorkerLogEntry> Logs { get; init; } = [];

    public WorkProfileSnapshot? Profile { get; init; }
}

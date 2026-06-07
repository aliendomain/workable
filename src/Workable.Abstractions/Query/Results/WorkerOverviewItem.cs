namespace Workable;

public sealed record WorkerOverviewItem(
    WorkerId Id,
    string DefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    long Revision,
    string Category,
    WorkerState State,
    WorkInterruptionReason? InterruptionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsFinal => this.State.IsFinal();

    public TimeSpan? QueueDuration { get; init; }

    public TimeSpan TotalExecutionDuration { get; init; }

    public DateTimeOffset? NextRunAt { get; init; }

    public static WorkerOverviewItem From(WorkerSummary worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.SubjectId,
            worker.ConcurrencyKey,
            worker.Identifiers,
            worker.Revision,
            worker.DefinitionCategory,
            worker.State,
            worker.InterruptionReason,
            worker.CreatedAt,
            worker.StateChangedAt,
            worker.UpdatedAt)
        {
            QueueDuration = worker.QueueDuration,
            TotalExecutionDuration = worker.TotalExecutionDuration,
            NextRunAt = worker.NextRunAt,
        };
}

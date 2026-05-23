using System.Text.Json;

namespace Workable;

internal static class WorkerEventPayloads
{
    public static JsonElement Create(
        WorkerSummary worker,
        IReadOnlyList<WorkerEventKey> keys,
        WorkAction? action = null,
        WorkActionStatus? actionStatus = null,
        WorkActionStatus? reconfigurationStatus = null,
        WorkerReconfiguration? reconfiguration = null,
        WorkCompletionStatus? completionStatus = null,
        WorkerIterationSnapshot? iteration = null,
        TimeSpan? recurrenceInterval = null,
        TimeSpan? retryDelay = null)
    {
        return JsonSerializer.SerializeToElement(
            new WorkerEventPayload(
                WorkerEventWorkerPayload.From(worker),
                keys,
                action,
                actionStatus,
                reconfigurationStatus,
                reconfiguration,
                completionStatus,
                iteration is null ? null : WorkerIterationEventPayload.From(iteration),
                recurrenceInterval,
                retryDelay),
            WorkEventJson.Options);
    }

    public static JsonElement CreatePurge(
        IReadOnlyList<WorkerId> workerIds,
        DateTimeOffset purgedAt)
    {
        return JsonSerializer.SerializeToElement(
            new WorkerPurgePayload(purgedAt, workerIds),
            WorkEventJson.Options);
    }

    private sealed record WorkerEventPayload(
        WorkerEventWorkerPayload Worker,
        IReadOnlyList<WorkerEventKey> Keys,
        WorkAction? Action = null,
        WorkActionStatus? ActionStatus = null,
        WorkActionStatus? ReconfigurationStatus = null,
        WorkerReconfiguration? Reconfiguration = null,
        WorkCompletionStatus? CompletionStatus = null,
        WorkerIterationEventPayload? Iteration = null,
        TimeSpan? RecurrenceInterval = null,
        TimeSpan? RetryDelay = null);

    private sealed record WorkerPurgePayload(
        DateTimeOffset PurgedAt,
        IReadOnlyList<WorkerId> WorkerIds);

    private sealed record WorkerEventWorkerPayload(
        WorkerId Id,
        long Revision,
        long StateSequence,
        WorkDefinitionId DefinitionId,
        string DefinitionName,
        string DefinitionCategory,
        WorkSubjectId? SubjectId,
        WorkConcurrencyKey? ConcurrencyKey,
        IReadOnlySet<WorkIdentifier> Identifiers,
        WorkerState State,
        WorkInterruptionReason? InterruptionReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset StateChangedAt,
        DateTimeOffset UpdatedAt)
    {
        public WorkerVersion Version => new(Id, Revision);

        public TimeSpan? QueueDuration { get; init; }

        public TimeSpan TotalExecutionDuration { get; init; }

        public DateTimeOffset? NextRunAt { get; init; }

        public static WorkerEventWorkerPayload From(WorkerSummary worker)
            => new(
                worker.Id,
                worker.Revision,
                worker.StateSequence,
                worker.DefinitionId,
                worker.DefinitionName,
                worker.DefinitionCategory,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers,
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

    private sealed record WorkerIterationEventPayload(
        long Sequence,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan ExecutionDuration,
        WorkCompletionStatus Status)
    {
        public static WorkerIterationEventPayload From(WorkerIterationSnapshot iteration)
            => new(
                iteration.Sequence,
                iteration.StartedAt,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                iteration.Status);
    }
}

internal sealed record WorkerEventKey(
    WorkKeyKind Kind,
    string Type,
    string Value);

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
                worker,
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
        WorkerSummary Worker,
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

using System.Text.Json;

namespace Workable;

internal sealed record WorkerEventRetainedLogSummary(
    int Total,
    int Critical,
    int Error,
    int Errors,
    int Warning,
    int Warnings,
    int Information,
    int Debug,
    int Trace);

internal sealed record WorkerEventRetainedTimelineSummary(
    int Total,
    int UserActionCount,
    int SystemEventCount,
    int FailureCount);

internal static class WorkerEventPayloads
{
    public static JsonElement Create(
        WorkerSummary worker,
        IReadOnlyList<WorkerEventKey> keys,
        WorkOrigin? origin = null,
        WorkAction? action = null,
        WorkActionStatus? actionStatus = null,
        WorkActionStatus? reconfigurationStatus = null,
        WorkerReconfiguration? reconfiguration = null,
        WorkCompletionStatus? completionStatus = null,
        WorkerIterationSnapshot? iteration = null,
        TimeSpan? recurrenceInterval = null,
        TimeSpan? retryDelay = null,
        WorkerLogEntry? logEntry = null,
        WorkerEventRetainedLogSummary? retainedLogSummary = null,
        WorkerEventRetainedTimelineSummary? retainedTimelineSummary = null)
    {
        return JsonSerializer.SerializeToElement(
            new WorkerEventPayload(
                WorkerEventWorkerPayload.From(worker, retainedLogSummary, retainedTimelineSummary),
                keys,
                origin is null ? null : WorkerEventOriginPayload.From(origin),
                action,
                actionStatus,
                reconfigurationStatus,
                reconfiguration,
                completionStatus,
                iteration is null ? null : WorkerIterationEventPayload.From(iteration),
                recurrenceInterval,
                retryDelay,
                logEntry is null ? null : WorkerEventLogPayload.From(logEntry)),
            WorkEventJson.Options);
    }

    public static JsonElement CreatePurge(
        IReadOnlyList<WorkerId> workerIds,
        DateTimeOffset purgedAt,
        WorkOrigin? origin = null)
    {
        return JsonSerializer.SerializeToElement(
            new WorkerPurgePayload(
                purgedAt,
                workerIds,
                origin is null ? null : WorkerEventOriginPayload.From(origin)),
            WorkEventJson.Options);
    }

    private sealed record WorkerEventPayload(
        WorkerEventWorkerPayload Worker,
        IReadOnlyList<WorkerEventKey> Keys,
        WorkerEventOriginPayload? Origin = null,
        WorkAction? Action = null,
        WorkActionStatus? ActionStatus = null,
        WorkActionStatus? ReconfigurationStatus = null,
        WorkerReconfiguration? Reconfiguration = null,
        WorkCompletionStatus? CompletionStatus = null,
        WorkerIterationEventPayload? Iteration = null,
        TimeSpan? RecurrenceInterval = null,
        TimeSpan? RetryDelay = null,
        WorkerEventLogPayload? Log = null);

    private sealed record WorkerPurgePayload(
        DateTimeOffset PurgedAt,
        IReadOnlyList<WorkerId> WorkerIds,
        WorkerEventOriginPayload? Origin = null);

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

        public int? RetryAttempt { get; init; }

        public TimeSpan? QueueDuration { get; init; }

        public TimeSpan TotalExecutionDuration { get; init; }

        public DateTimeOffset? NextRunAt { get; init; }

        public int ConfigDifferenceCount { get; init; }

        public WorkerEventRetainedLogSummary? LogSummary { get; init; }

        public WorkerEventRetainedTimelineSummary? TimelineSummary { get; init; }

        public static WorkerEventWorkerPayload From(
            WorkerSummary worker,
            WorkerEventRetainedLogSummary? retainedLogSummary = null,
            WorkerEventRetainedTimelineSummary? retainedTimelineSummary = null)
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
                RetryAttempt = worker.RetryAttempt,
                QueueDuration = worker.QueueDuration,
                TotalExecutionDuration = worker.TotalExecutionDuration,
                NextRunAt = worker.NextRunAt,
                ConfigDifferenceCount = worker.ConfigDifferenceCount,
                LogSummary = retainedLogSummary,
                TimelineSummary = retainedTimelineSummary,
            };
    }

    private sealed record WorkerIterationEventPayload(
        long Sequence,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan ExecutionDuration,
        WorkCompletionStatus Status,
        int AttemptCount)
    {
        public WorkOutput? Output { get; init; }

        public WorkerIterationFailure? Failure { get; init; }

        public static WorkerIterationEventPayload From(WorkerIterationSnapshot iteration)
            => new(
                iteration.Sequence,
                iteration.StartedAt,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                iteration.Status,
                iteration.AttemptCount)
            {
                Output = iteration.Status == WorkCompletionStatus.Executing ? null : iteration.Output,
                Failure = iteration.Failure,
            };
    }

    private sealed record WorkerEventLogPayload(
        string Id,
        string Category,
        string Level,
        WorkerEventLogEventIdPayload EventId,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage)
    {
        public static WorkerEventLogPayload From(WorkerLogEntry entry)
            => new(
                entry.Id.ToString("N"),
                entry.Category,
                entry.Level.ToString(),
                WorkerEventLogEventIdPayload.From(entry.EventId),
                entry.Message,
                entry.ExceptionType,
                entry.ExceptionMessage);
    }

    private sealed record WorkerEventLogEventIdPayload(
        int Id,
        string? Name)
    {
        public static WorkerEventLogEventIdPayload From(Microsoft.Extensions.Logging.EventId eventId)
            => new(eventId.Id, eventId.Name);
    }

    private sealed record WorkerEventOriginPayload(
        string Channel,
        WorkerEventOriginActorPayload? Actor,
        string? Description,
        string? Url)
    {
        public static WorkerEventOriginPayload From(WorkOrigin origin)
            => new(
                origin.Channel.ToString(),
                WorkerEventOriginActorPayload.From(origin.Actor),
                origin.Description,
                origin.Url);
    }

    private sealed record WorkerEventOriginActorPayload(
        string? Id,
        string? Name,
        string? Email)
    {
        public static WorkerEventOriginActorPayload? From(WorkActor actor)
            => string.IsNullOrWhiteSpace(actor.Id) &&
                string.IsNullOrWhiteSpace(actor.Name) &&
                string.IsNullOrWhiteSpace(actor.Email)
                ? null
                : new(actor.Id, actor.Name, actor.Email);
    }
}

internal sealed record WorkerEventKey(
    WorkKeyKind Kind,
    string Type,
    string Value);

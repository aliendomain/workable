using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Workable;

internal static class WorkerEventPayloads
{
    public static JsonElement Create(
        WorkerSummary worker,
        WorkInput? input = null,
        WorkOutput? output = null,
        WorkAction? action = null,
        WorkActionStatus? actionStatus = null,
        WorkActionStatus? reconfigurationStatus = null,
        WorkerReconfiguration? reconfiguration = null,
        WorkCompletionStatus? completionStatus = null,
        WorkerIterationSnapshot? iteration = null,
        TimeSpan? recurrenceInterval = null,
        WorkerLogEntry? log = null)
    {
        return JsonSerializer.SerializeToElement(
            new WorkerEventPayload(
                worker,
                input,
                output,
                action,
                actionStatus,
                reconfigurationStatus,
                reconfiguration,
                completionStatus,
                iteration,
                recurrenceInterval,
                log is null ? null : WorkerLogPayload.From(log)),
            WorkEventJson.Options);
    }

    private sealed record WorkerEventPayload(
        WorkerSummary Worker,
        WorkInput? Input = null,
        WorkOutput? Output = null,
        WorkAction? Action = null,
        WorkActionStatus? ActionStatus = null,
        WorkActionStatus? ReconfigurationStatus = null,
        WorkerReconfiguration? Reconfiguration = null,
        WorkCompletionStatus? CompletionStatus = null,
        WorkerIterationSnapshot? Iteration = null,
        TimeSpan? RecurrenceInterval = null,
        WorkerLogPayload? Log = null);

    private sealed record WorkerLogPayload(
        DateTimeOffset OccurredAt,
        string Category,
        LogLevel Level,
        int EventId,
        string? EventName,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage,
        IReadOnlyDictionary<string, object?>? Metadata)
    {
        public static WorkerLogPayload From(WorkerLogEntry entry)
            => new(
                entry.OccurredAt,
                entry.Category,
                entry.Level,
                entry.EventId.Id,
                entry.EventId.Name,
                entry.Message,
                entry.ExceptionType,
                entry.ExceptionMessage,
                entry.Metadata);
    }
}

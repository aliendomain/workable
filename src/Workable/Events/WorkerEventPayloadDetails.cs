namespace Workable;

internal sealed record WorkerEventPayloadDetails(
    bool IncludeInput = false,
    bool IncludeOutput = false,
    WorkAction? Action = null,
    WorkActionStatus? ActionStatus = null,
    WorkActionStatus? ReconfigurationStatus = null,
    WorkerReconfiguration? Reconfiguration = null,
    WorkCompletionStatus? CompletionStatus = null,
    bool IncludeLatestIteration = false,
    TimeSpan? RecurrenceInterval = null,
    TimeSpan? RetryDelay = null,
    WorkerLogEntry? Log = null);

namespace Workable;

internal sealed record WorkerEventPayloadDetails(
    WorkAction? Action = null,
    WorkActionStatus? ActionStatus = null,
    WorkActionStatus? ReconfigurationStatus = null,
    WorkerReconfiguration? Reconfiguration = null,
    WorkCompletionStatus? CompletionStatus = null,
    bool IncludeLatestIteration = false,
    TimeSpan? RecurrenceInterval = null,
    TimeSpan? RetryDelay = null);

namespace Workable;

public sealed record WorkerActivityEvent(
    string Id,
    DateTimeOffset At,
    WorkerActivityEventKind Kind,
    WorkerActivityEventCategory Category,
    WorkerActionHistoryKind? ActionHistoryKind,
    WorkAction? Action,
    WorkActionStatus? ActionStatus,
    WorkerState? State,
    long? Sequence,
    WorkCompletionStatus? IterationStatus,
    int? AttemptCount,
    TimeSpan? ExecutionDuration,
    WorkOrigin? Origin,
    WorkerIterationFailure? Failure);

public enum WorkerActivityEventKind
{
    ActionRequest,
    StateChange,
    Iteration,
}

public enum WorkerActivityEventCategory
{
    UserAction,
    SystemEvent,
    Failure,
}

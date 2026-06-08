namespace Workable;

/// <summary>
/// Represents one timeline event derived from retained worker history and iterations.
/// </summary>
/// <param name="Id">The stable identifier for the derived activity event.</param>
/// <param name="At">The timestamp associated with the activity event.</param>
/// <param name="Kind">The broad type of activity represented by the event.</param>
/// <param name="Category">The category used to present the activity event.</param>
/// <param name="ActionHistoryKind">The backing action-history kind, when the event came from action history.</param>
/// <param name="Action">The related worker action, when the event came from action history.</param>
/// <param name="ActionStatus">The related action status, when the event came from action history.</param>
/// <param name="State">The resulting worker state, when the event represents a state change.</param>
/// <param name="Sequence">The related iteration sequence, when applicable.</param>
/// <param name="IterationStatus">The related iteration completion status, when the event represents an iteration outcome.</param>
/// <param name="AttemptCount">The retained attempt count for an iteration event, when available.</param>
/// <param name="ExecutionDuration">The retained execution duration for an iteration event, when available.</param>
/// <param name="Origin">The caller origin associated with the event, when one exists.</param>
/// <param name="Failure">The derived failure details, when the event represents a failed iteration.</param>
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

/// <summary>
/// Identifies the broad type of derived worker activity event.
/// </summary>
public enum WorkerActivityEventKind
{
    /// <summary>
    /// The event represents an action or reconfiguration request.
    /// </summary>
    ActionRequest,

    /// <summary>
    /// The event represents a resulting worker state change.
    /// </summary>
    StateChange,

    /// <summary>
    /// The event represents a worker iteration lifecycle event.
    /// </summary>
    Iteration,
}

/// <summary>
/// Identifies how an activity event should be categorized for presentation.
/// </summary>
public enum WorkerActivityEventCategory
{
    /// <summary>
    /// The event was initiated by an explicit caller action.
    /// </summary>
    UserAction,

    /// <summary>
    /// The event represents ordinary system activity.
    /// </summary>
    SystemEvent,

    /// <summary>
    /// The event represents a failure outcome.
    /// </summary>
    Failure,
}

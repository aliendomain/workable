using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Selects which activity stream should be emphasized in the worker-overview landing payload.
/// </summary>
public enum WorkWorkerOverviewActivity
{
    /// <summary>
    /// Lets the server choose the most relevant activity stream.
    /// </summary>
    Auto,

    /// <summary>
    /// Prefers the worker log activity stream.
    /// </summary>
    Logs,

    /// <summary>
    /// Prefers the worker timeline activity stream.
    /// </summary>
    Timeline,
}

/// <summary>
/// Defines the sort direction used for worker-overview logs and timelines.
/// </summary>
public enum WorkWorkerOverviewSortDirection
{
    /// <summary>
    /// Sorts newest entries first.
    /// </summary>
    Desc,

    /// <summary>
    /// Sorts oldest entries first.
    /// </summary>
    Asc,
}

/// <summary>
/// Requests the HTTP landing payload for one worker-overview screen.
/// </summary>
public sealed record WorkWorkerOverviewCriteria(
    WorkWorkerOverviewActivity Activity = WorkWorkerOverviewActivity.Auto,
    int ActivityTake = 50,
    string? ActivityCursor = null,
    int RecentIterationTake = 25,
    WorkWorkerOverviewSortDirection LogSortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<LogLevel>? LogLevels = null,
    long? LogIterationSequence = null,
    WorkWorkerOverviewSortDirection TimelineSortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<WorkWorkerOverviewTimelineCategory>? TimelineCategories = null);

/// <summary>
/// Represents the HTTP landing payload for a worker detail screen.
/// </summary>
public sealed record WorkWorkerOverviewComponent(
    WorkWorkerOverviewActivity Activity,
    WorkWorkerOverviewWorker Worker,
    WorkInput? Input,
    WorkWorkerOverviewLatestIteration? LatestIteration,
    IReadOnlyList<WorkWorkerOverviewRecentIteration> RecentIterations,
    WorkWorkerOverviewLogSection Logs,
    WorkWorkerOverviewTimelineSection Timeline);

/// <summary>
/// Summarizes the current worker state and definition-level metadata used by the detail screen.
/// </summary>
public sealed record WorkWorkerOverviewWorker(
    WorkerId WorkerId,
    long Revision,
    long StateSequence,
    WorkerState State,
    bool IsFinal,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextRunAt,
    int? RetryAttempt,
    WorkWorkerOverviewOrigin CreatedOrigin,
    string DefinitionName,
    string DefinitionCategory,
    int ConfigDifferenceCount);

/// <summary>
/// Describes the channel and actor that created or affected a worker or timeline item.
/// </summary>
public sealed record WorkWorkerOverviewOrigin(
    WorkInvocationChannel Channel,
    WorkOriginSurface Surface = WorkOriginSurface.HostApplication,
    string? ActorId = null,
    string? ActorName = null,
    string? ActorEmail = null);

/// <summary>
/// Describes the latest retained iteration for the worker.
/// </summary>
public sealed record WorkWorkerOverviewLatestIteration(
    WorkerId WorkerId,
    long Sequence,
    WorkCompletionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? ExecutionDuration,
    WorkOutput? Output,
    WorkWorkerOverviewFailure? Failure,
    int AttemptCount = 1);

/// <summary>
/// Describes one retained recent iteration for the worker.
/// </summary>
public sealed record WorkWorkerOverviewRecentIteration(
    WorkerId WorkerId,
    long Sequence,
    WorkCompletionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? ExecutionDuration,
    int AttemptCount = 1);

/// <summary>
/// Describes a failure or exception surfaced in worker-overview data.
/// </summary>
public sealed record WorkWorkerOverviewFailure(
    WorkWorkerOverviewFailureKind Kind,
    string Message,
    string? Code = null,
    string? Target = null,
    string? ExceptionType = null,
    string? StackTrace = null,
    bool DeclaredByWork = false,
    WorkWorkerOverviewPendingState? PendingState = null);

/// <summary>
/// Identifies whether a worker-overview failure came from declared work failure or an unhandled exception.
/// </summary>
public enum WorkWorkerOverviewFailureKind
{
    /// <summary>
    /// The work declared a failure result.
    /// </summary>
    Failure,

    /// <summary>
    /// Execution failed because of an exception.
    /// </summary>
    Exception,
}

/// <summary>
/// Contains retained worker-log summary data and an optional paged log slice.
/// </summary>
public sealed record WorkWorkerOverviewLogSection(
    WorkWorkerOverviewLogSummary Summary,
    WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry>? Page);

/// <summary>
/// Summarizes retained worker log entries by log level.
/// </summary>
public sealed record WorkWorkerOverviewLogSummary(
    int Total,
    int Critical,
    int Error,
    int Errors,
    int Warning,
    int Warnings,
    int Information,
    int Debug,
    int Trace);

/// <summary>
/// Represents one retained worker log entry.
/// </summary>
public sealed record WorkWorkerOverviewLogEntry(
    string Id,
    DateTimeOffset OccurredAt,
    LogLevel Level,
    string Category,
    string Message,
    int EventId,
    string? EventName,
    string? ExceptionType,
    string? ExceptionMessage,
    long? Sequence = null,
    long? Ordinal = null);

/// <summary>
/// Contains retained worker-timeline summary data and an optional paged timeline slice.
/// </summary>
public sealed record WorkWorkerOverviewTimelineSection(
    WorkWorkerOverviewTimelineSummary Summary,
    WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem>? Page);

/// <summary>
/// Summarizes retained worker-timeline items by category.
/// </summary>
public sealed record WorkWorkerOverviewTimelineSummary(
    int Total,
    int UserActionCount,
    int SystemEventCount,
    int FailureCount);

/// <summary>
/// Represents one retained worker-timeline item.
/// </summary>
public sealed record WorkWorkerOverviewTimelineItem(
    string Id,
    DateTimeOffset At,
    WorkWorkerOverviewTimelineItemKind Kind,
    WorkWorkerOverviewTimelineCategory Category,
    WorkerActionHistoryKind? ActionHistoryKind,
    WorkAction? Action,
    WorkActionStatus? ActionStatus,
    WorkerState? State,
    long? Sequence,
    WorkCompletionStatus? IterationStatus,
    TimeSpan? ExecutionDuration,
    WorkWorkerOverviewOrigin? Origin,
    WorkWorkerOverviewFailure? Failure,
    WorkWorkerOverviewPendingState? PendingState = null,
    int? AttemptCount = null);

/// <summary>
/// Describes the worker's pending recurrence or retry state.
/// </summary>
public sealed record WorkWorkerOverviewPendingState(
    WorkWorkerOverviewPendingStateMode Mode,
    DateTimeOffset? NextRunAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt,
    int? RetryAttempt = null);

/// <summary>
/// Identifies why the worker is pending future execution.
/// </summary>
public enum WorkWorkerOverviewPendingStateMode
{
    /// <summary>
    /// The worker is waiting for the next recurrence interval.
    /// </summary>
    Recurrence,

    /// <summary>
    /// The worker is waiting for the next transient retry attempt.
    /// </summary>
    Retry,
}

/// <summary>
/// Identifies the kind of timeline item.
/// </summary>
public enum WorkWorkerOverviewTimelineItemKind
{
    /// <summary>
    /// A user or system action request.
    /// </summary>
    ActionRequest,

    /// <summary>
    /// A worker state transition.
    /// </summary>
    StateChange,

    /// <summary>
    /// An iteration lifecycle item.
    /// </summary>
    Iteration,
}

/// <summary>
/// Identifies the broad category of a worker timeline item.
/// </summary>
public enum WorkWorkerOverviewTimelineCategory
{
    /// <summary>
    /// User-initiated or operator-facing activity.
    /// </summary>
    UserAction,

    /// <summary>
    /// System-generated lifecycle activity.
    /// </summary>
    SystemEvent,

    /// <summary>
    /// Failure or exception activity.
    /// </summary>
    Failure,
}

/// <summary>
/// Represents one page of worker-overview items.
/// </summary>
public sealed record WorkWorkerOverviewPage<T>(
    IReadOnlyList<T> Items,
    bool HasMore,
    string? Cursor);

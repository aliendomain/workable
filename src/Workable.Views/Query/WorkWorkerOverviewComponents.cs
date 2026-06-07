using Microsoft.Extensions.Logging;

namespace Workable;

public enum WorkWorkerOverviewActivity
{
    Auto,
    Logs,
    Timeline,
}

public enum WorkWorkerOverviewSortDirection
{
    Desc,
    Asc,
}

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

public sealed record WorkWorkerOverviewComponent(
    WorkWorkerOverviewActivity Activity,
    WorkWorkerOverviewWorker Worker,
    WorkInput? Input,
    WorkWorkerOverviewLatestIteration? LatestIteration,
    IReadOnlyList<WorkWorkerOverviewRecentIteration> RecentIterations,
    WorkWorkerOverviewLogSection Logs,
    WorkWorkerOverviewTimelineSection Timeline);

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

public sealed record WorkWorkerOverviewOrigin(
    WorkInvocationChannel Channel,
    string? ActorId = null,
    string? ActorName = null,
    string? ActorEmail = null);

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

public sealed record WorkWorkerOverviewRecentIteration(
    WorkerId WorkerId,
    long Sequence,
    WorkCompletionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? ExecutionDuration,
    int AttemptCount = 1);

public sealed record WorkWorkerOverviewFailure(
    WorkWorkerOverviewFailureKind Kind,
    string Message,
    string? Code = null,
    string? Target = null,
    string? ExceptionType = null,
    string? StackTrace = null,
    bool DeclaredByWork = false,
    WorkWorkerOverviewPendingState? PendingState = null);

public enum WorkWorkerOverviewFailureKind
{
    Failure,
    Exception,
}

public sealed record WorkWorkerOverviewLogSection(
    WorkWorkerOverviewLogSummary Summary,
    WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry>? Page);

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

public sealed record WorkWorkerOverviewTimelineSection(
    WorkWorkerOverviewTimelineSummary Summary,
    WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem>? Page);

public sealed record WorkWorkerOverviewTimelineSummary(
    int Total,
    int UserActionCount,
    int SystemEventCount,
    int FailureCount);

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

public sealed record WorkWorkerOverviewPendingState(
    WorkWorkerOverviewPendingStateMode Mode,
    DateTimeOffset? NextRunAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset UpdatedAt,
    int? RetryAttempt = null);

public enum WorkWorkerOverviewPendingStateMode
{
    Recurrence,
    Retry,
}

public enum WorkWorkerOverviewTimelineItemKind
{
    ActionRequest,
    StateChange,
    Iteration,
}

public enum WorkWorkerOverviewTimelineCategory
{
    UserAction,
    SystemEvent,
    Failure,
}

public sealed record WorkWorkerOverviewPage<T>(
    IReadOnlyList<T> Items,
    bool HasMore,
    string? Cursor);


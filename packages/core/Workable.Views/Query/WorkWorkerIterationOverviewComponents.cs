using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Selects which retained iteration activity stream should be materialized in the landing payload.
/// </summary>
public enum WorkWorkerIterationOverviewActivity
{
    /// <summary>
    /// Lets the server choose the most relevant activity stream.
    /// </summary>
    Auto,

    /// <summary>
    /// Returns only summary data for retained messages and logs.
    /// </summary>
    None,

    /// <summary>
    /// Includes the retained message page in the landing payload.
    /// </summary>
    Messages,

    /// <summary>
    /// Includes the retained log page in the landing payload.
    /// </summary>
    Logs,
}

/// <summary>
/// Requests the HTTP landing payload for one worker-iteration detail screen.
/// </summary>
public sealed record WorkWorkerIterationOverviewCriteria(
    WorkWorkerIterationOverviewActivity Activity = WorkWorkerIterationOverviewActivity.Auto,
    int ActivityTake = 50,
    string? ActivityCursor = null,
    bool IncludeInput = true,
    bool IncludeOutput = true,
    bool IncludeProfile = true,
    WorkWorkerOverviewSortDirection MessageSortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<WorkMessageSeverity>? MessageSeverities = null,
    WorkWorkerOverviewSortDirection LogSortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<LogLevel>? LogLevels = null);

/// <summary>
/// Represents the HTTP landing payload for one worker-iteration detail screen.
/// </summary>
public sealed record WorkWorkerIterationOverviewComponent(
    WorkWorkerIterationOverviewActivity Activity,
    WorkSystemCapabilities Capabilities,
    WorkWorkerIterationOverviewWorker Worker,
    WorkInput? Input,
    WorkWorkerIterationOverviewIteration Iteration,
    WorkWorkerIterationOverviewMessageSection Messages,
    WorkWorkerIterationOverviewLogSection Logs);

/// <summary>
/// Summarizes the worker-level identity and key metadata used by the iteration detail screen.
/// </summary>
public sealed record WorkWorkerIterationOverviewWorker(
    WorkerId WorkerId,
    string DefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    bool ProfilingEnabled);

/// <summary>
/// Describes the retained iteration snapshot embedded in the overview landing payload.
/// </summary>
public sealed record WorkWorkerIterationOverviewIteration(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    DateTimeOffset OccurredAt,
    WorkCompletionStatus Status,
    int AttemptCount,
    bool IsFinal,
    WorkOutput? Output,
    WorkerIterationFailure? Failure,
    WorkProfileSnapshot? Profile);

/// <summary>
/// Contains retained structured-message summary data and an optional paged message slice.
/// </summary>
public sealed record WorkWorkerIterationOverviewMessageSection(
    WorkIterationMessageSummary Summary,
    WorkIterationMessagePage? Page);

/// <summary>
/// Contains retained worker-log summary data and an optional paged log slice.
/// </summary>
public sealed record WorkWorkerIterationOverviewLogSection(
    WorkWorkerOverviewLogSummary Summary,
    WorkWorkerOverviewPage<WorkerLogEntry>? Page);

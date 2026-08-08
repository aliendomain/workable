using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Describes the live panel state for a worker-overview realtime subscription.
/// </summary>
public sealed record WorkWorkerOverviewRealtimeCriteria(
    string WorkerControls = WorkComponentShapes.Compact,
    string WorkerLogs = WorkComponentShapes.Compact,
    string WorkerDuration = WorkComponentShapes.Compact,
    string WorkerTimeline = WorkComponentShapes.Compact,
    WorkWorkerOverviewSortDirection LogSortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<LogLevel>? LogLevels = null,
    long? LogIterationSequence = null,
    WorkWorkerOverviewSortDirection TimelineSortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<WorkWorkerOverviewTimelineCategory>? TimelineCategories = null);

/// <summary>
/// Represents the full current worker-overview realtime state used to seed a subscription.
/// </summary>
public sealed record WorkWorkerOverviewRealtimeState(
    WorkWorkerOverviewWorker Worker,
    WorkWorkerOverviewLatestIteration? LatestIteration,
    WorkWorkerOverviewLogSummary? LogSummary,
    IReadOnlyList<WorkWorkerOverviewLogEntry> LogEntries,
    IReadOnlyList<WorkWorkerOverviewRecentIteration> RecentIterations,
    WorkWorkerOverviewTimelineSummary? TimelineSummary,
    IReadOnlyList<WorkWorkerOverviewTimelineItem> TimelineItems);

/// <summary>
/// Represents a synchronized worker-overview latest-state update or refresh instruction.
/// </summary>
public sealed record WorkWorkerOverviewRealtimeUpdate(
    DateTimeOffset GeneratedAt,
    WorkWorkerOverviewWorker? Worker = null,
    WorkWorkerOverviewLatestIteration? LatestIteration = null,
    WorkWorkerOverviewLogSummary? LogSummary = null,
    IReadOnlyList<WorkWorkerOverviewLogEntry>? LogEntries = null,
    IReadOnlyList<WorkWorkerOverviewRecentIteration>? RecentIterations = null,
    WorkWorkerOverviewTimelineSummary? TimelineSummary = null,
    IReadOnlyList<WorkWorkerOverviewTimelineItem>? TimelineItems = null,
    bool RequiresRefresh = false,
    string? RefreshReason = null);

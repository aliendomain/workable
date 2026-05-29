using Microsoft.Extensions.Logging;

namespace Workable;

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

public sealed record WorkWorkerOverviewRealtimeState(
    WorkWorkerOverviewWorker Worker,
    WorkWorkerOverviewLatestIteration? LatestIteration,
    WorkWorkerOverviewLogSummary? LogSummary,
    IReadOnlyList<WorkWorkerOverviewLogEntry> LogEntries,
    IReadOnlyList<WorkWorkerOverviewRecentIteration> RecentIterations,
    WorkWorkerOverviewTimelineSummary? TimelineSummary,
    IReadOnlyList<WorkWorkerOverviewTimelineItem> TimelineItems);

public sealed record WorkWorkerOverviewRealtimeUpdate(
    DateTimeOffset GeneratedAt,
    WorkWorkerOverviewWorker? Worker = null,
    WorkWorkerOverviewLatestIteration? LatestIteration = null,
    WorkWorkerOverviewLogSummary? LogSummary = null,
    IReadOnlyList<WorkWorkerOverviewLogEntry>? LogEntries = null,
    IReadOnlyList<WorkWorkerOverviewRecentIteration>? RecentIterations = null,
    WorkWorkerOverviewTimelineSummary? TimelineSummary = null,
    IReadOnlyList<WorkWorkerOverviewTimelineItem>? TimelineItems = null);

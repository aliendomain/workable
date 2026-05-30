using Microsoft.Extensions.Logging;

namespace Workable;

public sealed record WorkIterationMessageCriteria(
    int Take = 50,
    string? Cursor = null,
    WorkWorkerOverviewSortDirection SortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<WorkMessageSeverity>? Severities = null);

public sealed record WorkIterationLogCriteria(
    int Take = 50,
    string? Cursor = null,
    WorkWorkerOverviewSortDirection SortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<LogLevel>? Levels = null);

public sealed record WorkIterationMessageSection(
    WorkIterationMessageSummary Summary,
    WorkIterationMessagePage Page);

public sealed record WorkIterationMessageSummary(
    int Total,
    int Critical,
    int Error,
    int Errors,
    int Warning,
    int Warnings,
    int Information,
    int Debug,
    int Trace);

public sealed record WorkIterationMessagePage(
    IReadOnlyList<WorkMessage> Items,
    bool HasMore,
    string? Cursor);

public sealed record WorkIterationLogSection(
    WorkWorkerOverviewLogSummary Summary,
    WorkWorkerOverviewPage<WorkerLogEntry> Page);

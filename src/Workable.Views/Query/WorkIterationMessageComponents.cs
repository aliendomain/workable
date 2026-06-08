using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Requests a paged slice of structured iteration messages.
/// </summary>
public sealed record WorkIterationMessageCriteria(
    int Take = 50,
    string? Cursor = null,
    WorkWorkerOverviewSortDirection SortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<WorkMessageSeverity>? Severities = null);

/// <summary>
/// Requests a paged slice of retained iteration log entries.
/// </summary>
public sealed record WorkIterationLogCriteria(
    int Take = 50,
    string? Cursor = null,
    WorkWorkerOverviewSortDirection SortDirection = WorkWorkerOverviewSortDirection.Desc,
    IReadOnlyList<LogLevel>? Levels = null);

/// <summary>
/// Contains the summary and page of structured iteration messages.
/// </summary>
public sealed record WorkIterationMessageSection(
    WorkIterationMessageSummary Summary,
    WorkIterationMessagePage Page);

/// <summary>
/// Summarizes retained structured iteration messages by severity.
/// </summary>
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

/// <summary>
/// Represents one page of structured iteration messages.
/// </summary>
public sealed record WorkIterationMessagePage(
    IReadOnlyList<WorkMessage> Items,
    bool HasMore,
    string? Cursor);

/// <summary>
/// Contains the summary and page of retained iteration log entries.
/// </summary>
public sealed record WorkIterationLogSection(
    WorkWorkerOverviewLogSummary Summary,
    WorkWorkerOverviewPage<WorkerLogEntry> Page);

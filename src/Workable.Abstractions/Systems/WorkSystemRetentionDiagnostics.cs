namespace Workable;

/// <summary>
/// Describes final-worker retention tracking and purge scheduling.
/// </summary>
/// <param name="TrackedFinalWorkerCount">The number of final-state workers currently tracked for retention decisions.</param>
/// <param name="ScheduledPurgeCount">The number of workers currently scheduled for time-based purge.</param>
/// <param name="ScheduledPurgeHighWaterMark">The highest scheduled purge queue size observed since the last reset.</param>
/// <param name="OldestScheduledPurgeDueAt">The due time of the oldest scheduled purge, when one exists.</param>
/// <param name="OldestDuePurgeAge">How long the oldest due purge has been waiting.</param>
/// <param name="PendingCountRetentionDefinitionCount">The number of definitions waiting for count-based cleanup.</param>
/// <param name="SystemCountRetentionPending">Whether system-wide count-based cleanup is pending.</param>
/// <param name="LastRunAt">The time the most recent retention scheduler run started or completed.</param>
/// <param name="LastRunDuration">The duration of the most recent retention scheduler run.</param>
/// <param name="LastPurgedCount">The number of workers purged in the most recent retention run.</param>
/// <param name="TotalPurgedCount">The total number of workers purged by retention.</param>
/// <param name="SchedulerFailureType">The exception type from the most recent scheduler failure, when one occurred.</param>
/// <param name="SchedulerFailureMessage">The message from the most recent scheduler failure, when one occurred.</param>
public sealed record WorkSystemRetentionDiagnostics(
    int TrackedFinalWorkerCount,
    int ScheduledPurgeCount,
    int ScheduledPurgeHighWaterMark,
    DateTimeOffset? OldestScheduledPurgeDueAt,
    TimeSpan OldestDuePurgeAge,
    int PendingCountRetentionDefinitionCount,
    bool SystemCountRetentionPending,
    DateTimeOffset? LastRunAt,
    TimeSpan LastRunDuration,
    int LastPurgedCount,
    long TotalPurgedCount,
    string? SchedulerFailureType,
    string? SchedulerFailureMessage)
{
    /// <summary>
    /// Gets a value indicating whether the retention scheduler has recorded an internal failure.
    /// </summary>
    public bool HasSchedulerFailure => this.SchedulerFailureType is not null;
}

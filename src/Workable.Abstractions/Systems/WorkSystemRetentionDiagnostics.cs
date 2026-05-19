namespace Workable;

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
    public bool HasSchedulerFailure => this.SchedulerFailureType is not null;
}

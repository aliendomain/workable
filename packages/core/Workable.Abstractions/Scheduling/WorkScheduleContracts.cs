namespace Workable;

/// <summary>
/// Describes when runtime-scheduled work should run.
/// </summary>
/// <param name="FirstRunAt">The first UTC-aware instant at which an instant or interval schedule becomes due, or the earliest eligible instant for a cron schedule.</param>
/// <param name="Interval">The interval between recurring occurrences, or null for a one-time schedule.</param>
/// <param name="RunMissedExecution">Whether one overdue occurrence should run after the work system was down.</param>
/// <param name="CronExpression">A standard five-field cron expression, or null when recurrence is not calendar-based.</param>
/// <param name="TimeZoneId">The time-zone identifier used to evaluate <paramref name="CronExpression"/>.</param>
public sealed record WorkScheduleTiming(
    DateTimeOffset FirstRunAt,
    TimeSpan? Interval = null,
    bool RunMissedExecution = true,
    string? CronExpression = null,
    string? TimeZoneId = null)
{
    /// <summary>
    /// Gets the maximum accepted cron-expression length.
    /// </summary>
    public const int MaximumCronExpressionLength = 256;

    /// <summary>
    /// Gets the maximum accepted time-zone identifier length.
    /// </summary>
    public const int MaximumTimeZoneIdLength = 256;

    /// <summary>
    /// Creates one one-time schedule.
    /// </summary>
    public static WorkScheduleTiming Once(DateTimeOffset runAt, bool runMissedExecution = true)
        => new(runAt, null, runMissedExecution);

    /// <summary>
    /// Creates a recurring interval schedule.
    /// </summary>
    public static WorkScheduleTiming Every(
        TimeSpan interval,
        DateTimeOffset? firstRunAt = null,
        bool runMissedExecution = true)
        => new(firstRunAt ?? DateTimeOffset.UtcNow + interval, interval, runMissedExecution);

    /// <summary>
    /// Creates a recurring calendar schedule from a standard five-field cron expression.
    /// </summary>
    public static WorkScheduleTiming Cron(
        string expression,
        string timeZoneId = "UTC",
        DateTimeOffset? startsAt = null,
        bool runMissedExecution = true)
        => new(
            startsAt ?? DateTimeOffset.UtcNow,
            Interval: null,
            runMissedExecution,
            expression,
            timeZoneId);

    /// <summary>
    /// Gets whether this is a recurring schedule.
    /// </summary>
    public bool IsRecurring => this.Interval is not null || this.CronExpression is not null;

    /// <summary>
    /// Gets whether recurrence is defined by a cron expression.
    /// </summary>
    public bool IsCron => this.CronExpression is not null;
}

/// <summary>
/// Requests a runtime schedule for a registered work definition.
/// </summary>
public sealed record WorkScheduleRequest(
    string DefinitionName,
    WorkScheduleTiming Timing,
    WorkInput? Input = null,
    WorkerOptions? WorkerOptions = null);

/// <summary>
/// Identifies the lifecycle state of a runtime schedule.
/// </summary>
public enum WorkScheduleStatus
{
    Active,
    Completed,
    Canceled,
}

/// <summary>
/// Describes one runtime schedule.
/// </summary>
public sealed record WorkScheduleSnapshot(
    WorkScheduleId Id,
    string? WorkSystemName,
    string DefinitionName,
    WorkScheduleTiming Timing,
    WorkInput? Input,
    WorkerOptions? WorkerOptions,
    WorkScheduleStatus Status,
    DateTimeOffset CreatedAt,
    WorkActor CreatedBy,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? CanceledAt,
    WorkActor? CanceledBy);

/// <summary>
/// Describes schedule metadata without returning the retained input, worker options, or request context.
/// </summary>
public sealed record WorkScheduleSummary(
    WorkScheduleId Id,
    string? WorkSystemName,
    string DefinitionName,
    WorkScheduleTiming Timing,
    WorkScheduleStatus Status,
    DateTimeOffset CreatedAt,
    WorkActor CreatedBy,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? CanceledAt,
    WorkActor? CanceledBy);

/// <summary>
/// Selects schedules from one work system.
/// </summary>
public sealed record WorkScheduleCriteria(
    string? DefinitionName = null,
    WorkScheduleStatus? Status = null,
    int Take = 100)
{
    public const int MaximumTake = 1_000;
}

/// <summary>
/// Returns schedules visible to the caller.
/// </summary>
public sealed record WorkScheduleQueryResult(IReadOnlyList<WorkScheduleSummary> Schedules);

/// <summary>
/// Identifies the immediate result of schedule creation.
/// </summary>
public enum WorkScheduleCreationStatus
{
    Accepted,
    Invalid,
    Unauthorized,
    NotFound,
    LimitReached,
    Unavailable,
}

/// <summary>
/// Returns the immediate result of schedule creation.
/// </summary>
public sealed record WorkScheduleCreationOutcome(
    WorkScheduleCreationStatus Status,
    WorkScheduleSnapshot? Schedule,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkScheduleCreationStatus.Accepted;
}

/// <summary>
/// Identifies the immediate result of schedule cancellation.
/// </summary>
public enum WorkScheduleCancellationStatus
{
    Accepted,
    Invalid,
    NotFound,
    Unauthorized,
    Conflict,
    Unavailable,
}

/// <summary>
/// Returns the immediate result of schedule cancellation.
/// </summary>
public sealed record WorkScheduleCancellationOutcome(
    WorkScheduleCancellationStatus Status,
    WorkScheduleId ScheduleId,
    WorkScheduleSnapshot? Schedule,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsAccepted => this.Status == WorkScheduleCancellationStatus.Accepted;
}

/// <summary>
/// Identifies what happened when one scheduled occurrence became due.
/// </summary>
public enum WorkScheduleOccurrenceStatus
{
    Accepted,
    Rejected,
    Skipped,
    Failed,
}

/// <summary>
/// Describes one retained schedule dispatch occurrence.
/// </summary>
public sealed record WorkScheduleOccurrence(
    Guid OccurrenceId,
    WorkScheduleId ScheduleId,
    DateTimeOffset ScheduledAt,
    DateTimeOffset AttemptedAt,
    WorkScheduleOccurrenceStatus Status,
    WorkQueueStatus? QueueStatus,
    WorkerId? WorkerId,
    IReadOnlyList<WorkMessage> Messages,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Returns retained occurrences visible to the caller.
/// </summary>
public sealed record WorkScheduleOccurrenceQueryResult(
    IReadOnlyList<WorkScheduleOccurrence> Occurrences);

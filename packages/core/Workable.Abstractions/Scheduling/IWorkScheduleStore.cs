namespace Workable;

/// <summary>
/// Persists runtime schedules and coordinates due-occurrence claims across hosts.
/// </summary>
public interface IWorkScheduleStore : IWorkScheduleHostPresenceStore
{
    Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default);

    Task<WorkScheduleStoreCreationStatus> Create(
        WorkScheduleStoreCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkSchedulePersistenceRecord?> Get(
        WorkScheduleStoreReadRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkScheduleSummary>> List(
        WorkScheduleStoreListRequest request,
        CancellationToken cancellationToken = default);

    async Task<WorkScheduleStoreUpcomingResult> ListUpcoming(
        WorkScheduleStoreUpcomingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var activeSchedules = new List<WorkScheduleSummary>();
        WorkScheduleCursor? cursor = null;
        var followedCursors = new HashSet<WorkScheduleCursor>();
        while (activeSchedules.Count < WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount)
        {
            var take = Math.Min(
                WorkScheduleCriteria.MaximumTake,
                WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount - activeSchedules.Count);
            var active = await this.List(
                new(
                    request.WorkSystemName,
                    Status: WorkScheduleStatus.Active,
                    Take: take,
                    DefinitionNames: request.DefinitionNames,
                    Cursor: cursor),
                cancellationToken);
            if (active.Count > take)
            {
                throw new InvalidOperationException("The schedule store returned more rows than requested.");
            }

            activeSchedules.AddRange(active);
            if (active.Count < take)
            {
                break;
            }

            var last = active[^1];
            cursor = new(last.CreatedAt, last.Id);
            if (!followedCursors.Add(cursor))
            {
                throw new InvalidOperationException("The schedule store returned a repeated continuation cursor.");
            }
        }

        if (activeSchedules.Count == WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount)
        {
            var overflow = await this.List(
                new(
                    request.WorkSystemName,
                    Status: WorkScheduleStatus.Active,
                    Take: 1,
                    DefinitionNames: request.DefinitionNames,
                    Cursor: cursor),
                cancellationToken);
            if (overflow.Count != 0)
            {
                throw new NotSupportedException(
                    $"The default upcoming-schedule query supports at most " +
                    $"{WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount} readable active schedules. " +
                    "Override IWorkScheduleStore.ListUpcoming for larger schedule sets.");
            }
        }

        var upcoming = activeSchedules
            .Where(static schedule => schedule.NextRunAt is not null)
            .OrderBy(static schedule => schedule.NextRunAt)
            .ThenBy(static schedule => schedule.Id.Value)
            .Where(schedule => request.Cursor is null ||
                schedule.NextRunAt > request.Cursor.NextRunAt ||
                (schedule.NextRunAt == request.Cursor.NextRunAt && schedule.Id.Value.CompareTo(request.Cursor.ScheduleId.Value) > 0))
            .Take(request.Take)
            .ToArray();
        return new(
            upcoming,
            activeSchedules.Count,
            activeSchedules.Count(static schedule => schedule.NextRunAt is not null),
            activeSchedules.Count(static schedule =>
                schedule.Timing.Interval is not null || schedule.Timing.CronExpression is not null));
    }

    async Task<WorkScheduleStoreOverviewResult> GetOverview(
        WorkScheduleStoreOverviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var recent = await this.List(
            new(
                request.WorkSystemName,
                Take: request.RecentScheduleTake + 1,
                DefinitionNames: request.DefinitionNames),
            cancellationToken);
        var recentPage = CreateRecentPage(recent, request.RecentScheduleTake);
        var upcoming = await this.ListUpcoming(
            new(
                request.WorkSystemName,
                request.UpcomingScheduleTake + 1,
                DefinitionNames: request.DefinitionNames),
            cancellationToken);
        var upcomingPage = CreateUpcomingPage(upcoming, request.UpcomingScheduleTake);
        WorkScheduleSummary? selected = null;
        if (request.SelectedScheduleId is { } requestedId)
        {
            selected = recentPage.Schedules.Concat(upcomingPage.Schedules)
                .FirstOrDefault(schedule => schedule.Id == requestedId);
            if (selected is null)
            {
                var requested = await this.Get(new(request.WorkSystemName, requestedId), cancellationToken);
                if (requested is not null && IsDefinitionVisible(requested.Schedule.DefinitionName, request.DefinitionNames))
                {
                    selected = ToSummary(requested.Schedule);
                }
            }
        }
        selected ??= recentPage.Schedules.FirstOrDefault();

        var occurrences = selected is null
            ? []
            : await this.ListOccurrences(
                new(
                    request.WorkSystemName,
                    selected.Id,
                    request.OccurrenceTake,
                    request.MaximumOccurrencePayloadBytes),
                cancellationToken);
        return new(recentPage, upcomingPage, selected, occurrences);

        static WorkScheduleQueryResult CreateRecentPage(
            IReadOnlyList<WorkScheduleSummary> schedules,
            int take)
        {
            if (schedules.Count <= take)
            {
                return new(schedules);
            }

            var page = schedules.Take(take).ToArray();
            var last = page[^1];
            return new(page, new(last.CreatedAt, last.Id));
        }

        static WorkScheduleUpcomingQueryResult CreateUpcomingPage(
            WorkScheduleStoreUpcomingResult result,
            int take)
        {
            if (result.Schedules.Count <= take)
            {
                return new(
                    result.Schedules,
                    null,
                    result.ActiveScheduleCount,
                    result.UpcomingScheduleCount,
                    result.RecurringScheduleCount);
            }

            var page = result.Schedules.Take(take).ToArray();
            var last = page[^1];
            return new(
                page,
                new(last.NextRunAt!.Value, last.Id),
                result.ActiveScheduleCount,
                result.UpcomingScheduleCount,
                result.RecurringScheduleCount);
        }

        static bool IsDefinitionVisible(string definitionName, IReadOnlySet<string>? definitionNames)
            => definitionNames is null || definitionNames.Contains(definitionName);

        static WorkScheduleSummary ToSummary(WorkScheduleSnapshot schedule)
            => new(
                schedule.Id,
                schedule.WorkSystemName,
                schedule.DefinitionName,
                schedule.Timing,
                schedule.Status,
                schedule.CreatedAt,
                schedule.CreatedBy,
                schedule.NextRunAt,
                schedule.LastRunAt,
                schedule.CanceledAt,
                schedule.CanceledBy);
    }

    Task<WorkSchedulePersistenceRecord?> Cancel(
        WorkScheduleStoreCancelRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
        WorkScheduleClaimRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> BeginDispatch(
        WorkScheduleDispatchStart dispatch,
        CancellationToken cancellationToken = default);

    Task CompleteClaim(
        WorkScheduleClaimCompletion completion,
        CancellationToken cancellationToken = default);

    Task ReleaseClaim(
        WorkScheduleClaimRelease release,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
        WorkScheduleOccurrenceReadRequest request,
        CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredOccurrences(
        WorkScheduleOccurrenceExpirationRequest request,
        CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredSchedules(
        WorkScheduleExpirationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Records scheduler-host availability so missed executions are evaluated across all hosts in a work system.
/// </summary>
public interface IWorkScheduleHostPresenceStore
{
    Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
        WorkScheduleClaimRequest request,
        WorkScheduleHostObservation observation,
        CancellationToken cancellationToken = default);

    Task EndHost(
        WorkScheduleHostEnd hostEnd,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
        WorkScheduleHostAvailabilityRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkScheduleHostObservation(
    string? WorkSystemName,
    Guid HostRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset ObservedAt,
    DateTimeOffset AvailableThrough,
    DateTimeOffset DeleteEndedBefore);

public sealed record WorkScheduleHostEnd(
    string? WorkSystemName,
    Guid HostRunId,
    DateTimeOffset EndedAt);

public sealed record WorkScheduleHostAvailabilityRequest(
    string? WorkSystemName,
    IReadOnlySet<DateTimeOffset> ScheduledTimes);

public sealed record WorkScheduleStoreInitializationContext(string? WorkSystemName);

public sealed record WorkSchedulePersistenceRecord(
    WorkScheduleSnapshot Schedule,
    WorkRequestContext RequestContext,
    WorkScheduleExecutionGrant ExecutionGrant)
{
    public long SerializedPayloadBytes { get; init; }
}

public sealed record WorkScheduleExecutionGrant(
    bool AllowsFullProfileCapture,
    string DefinitionSecurityVersion = WorkDefinition.DefaultScheduleSecurityVersion)
{
    public static WorkScheduleExecutionGrant Unrestricted { get; } = new(
        true,
        WorkDefinition.DefaultScheduleSecurityVersion);
}

public sealed record WorkScheduleStoreCreateRequest(
    WorkSchedulePersistenceRecord Record,
    long PayloadSizeBytes,
    long MaximumSchedulePayloadBytes,
    int MaximumActiveSchedules,
    int MaximumActiveSchedulesPerDefinition,
    int MaximumActiveSchedulesPerActor,
    int MaximumRetainedSchedules,
    int MaximumRetainedSchedulesPerActor,
    long MaximumRetainedPayloadBytes,
    long MaximumRetainedPayloadBytesPerActor,
    long CancellationPayloadReserveBytes = 0)
{
    /// <summary>
    /// Gets the payload budget reserved on every schedule for bounded cancellation-actor audit data.
    /// </summary>
    public const long CancellationActorPayloadReserveBytes = 10_000;
}

public enum WorkScheduleStoreCreationStatus
{
    Accepted,
    InvalidDefinitionName,
    PayloadTooLarge,
    SystemLimitReached,
    DefinitionLimitReached,
    RetainedSystemLimitReached,
    RetainedActorLimitReached,
    RetainedSystemBytesLimitReached,
    RetainedActorBytesLimitReached,
    ActiveActorLimitReached,
}

public sealed record WorkScheduleStoreReadRequest(string? WorkSystemName, WorkScheduleId ScheduleId);

public sealed record WorkScheduleStoreListRequest(
    string? WorkSystemName,
    string? DefinitionName = null,
    WorkScheduleStatus? Status = null,
    int Take = 100,
    IReadOnlySet<string>? DefinitionNames = null,
    WorkScheduleCursor? Cursor = null);

public sealed record WorkScheduleStoreOverviewRequest(
    string? WorkSystemName,
    WorkScheduleId? SelectedScheduleId = null,
    int RecentScheduleTake = 100,
    int UpcomingScheduleTake = 100,
    int OccurrenceTake = 50,
    long MaximumOccurrencePayloadBytes = 4_194_304,
    IReadOnlySet<string>? DefinitionNames = null);

public sealed record WorkScheduleStoreOverviewResult(
    WorkScheduleQueryResult Recent,
    WorkScheduleUpcomingQueryResult Upcoming,
    WorkScheduleSummary? SelectedSchedule,
    IReadOnlyList<WorkScheduleOccurrence> Occurrences);

public sealed record WorkScheduleStoreUpcomingRequest(
    string? WorkSystemName,
    int Take = 100,
    WorkScheduleUpcomingCursor? Cursor = null,
    IReadOnlySet<string>? DefinitionNames = null)
{
    /// <summary>
    /// Bounds the compatibility implementation's active-schedule scan to one maximum list page.
    /// Providers supporting larger active sets must override <see cref="IWorkScheduleStore.ListUpcoming"/>.
    /// </summary>
    public const int MaximumDefaultScanCount = WorkScheduleCriteria.MaximumTake;
}

public sealed record WorkScheduleStoreUpcomingResult(
    IReadOnlyList<WorkScheduleSummary> Schedules,
    int ActiveScheduleCount,
    int UpcomingScheduleCount,
    int RecurringScheduleCount);

public sealed record WorkScheduleStoreCancelRequest(
    string? WorkSystemName,
    WorkScheduleId ScheduleId,
    DateTimeOffset CanceledAt,
    WorkActor CanceledBy);

public sealed record WorkScheduleClaimRequest(
    string? WorkSystemName,
    DateTimeOffset DueAt,
    TimeSpan LeaseDuration,
    int MaximumCount = 25,
    long MaximumPayloadBytes = 8_388_608);

public sealed record WorkScheduleClaim(
    Guid LeaseId,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset ScheduledAt,
    WorkSchedulePersistenceRecord Record);

public sealed record WorkScheduleDispatchStart(
    string? WorkSystemName,
    WorkScheduleId ScheduleId,
    Guid LeaseId,
    DateTimeOffset StartedAt,
    DateTimeOffset LeaseExpiresAt);

public sealed record WorkScheduleClaimCompletion(
    string? WorkSystemName,
    WorkScheduleId ScheduleId,
    Guid LeaseId,
    WorkScheduleOccurrence Occurrence,
    WorkScheduleStatus ScheduleStatus,
    DateTimeOffset? NextRunAt,
    int MaximumRetainedOccurrences,
    long MaximumRetainedOccurrencePayloadBytes);

public sealed record WorkScheduleClaimRelease(
    string? WorkSystemName,
    WorkScheduleId ScheduleId,
    Guid LeaseId);

public sealed record WorkScheduleOccurrenceReadRequest(
    string? WorkSystemName,
    WorkScheduleId ScheduleId,
    int Take = 100,
    long MaximumPayloadBytes = 4_194_304)
{
    public const int MaximumTake = 100;
}

public sealed record WorkScheduleOccurrenceExpirationRequest(
    string? WorkSystemName,
    DateTimeOffset ExpiresBefore,
    int MaximumCount = 1_000);

public sealed record WorkScheduleExpirationRequest(
    string? WorkSystemName,
    DateTimeOffset FinalizedBefore,
    int MaximumCount = 1_000);

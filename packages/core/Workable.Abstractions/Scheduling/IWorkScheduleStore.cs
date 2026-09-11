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
    IReadOnlySet<string>? DefinitionNames = null);

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

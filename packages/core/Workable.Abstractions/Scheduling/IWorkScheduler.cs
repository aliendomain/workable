namespace Workable;

/// <summary>
/// Creates, reads, and cancels runtime schedules within one Workable system.
/// </summary>
public interface IWorkScheduler
{
    /// <summary>
    /// Creates a schedule after applying the same authorization used to queue the target work.
    /// </summary>
    Task<WorkScheduleCreationOutcome> Create(
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a schedule with typed input serialized using Workable's default JSON contract.
    /// </summary>
    Task<WorkScheduleCreationOutcome> Create<TInput>(
        string definitionName,
        TInput input,
        WorkScheduleTiming timing,
        WorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a readable schedule by identifier.
    /// </summary>
    Task<WorkScheduleSnapshot?> Get(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists readable schedules.
    /// </summary>
    Task<WorkScheduleQueryResult> List(
        WorkScheduleCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists readable active schedules with a next execution, ordered by due time.
    /// </summary>
    Task<WorkScheduleUpcomingQueryResult> ListUpcoming(
        int take = 100,
        WorkScheduleUpcomingCursor? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the bounded schedule-management page in one store operation.
    /// </summary>
    Task<WorkScheduleOverviewResult> GetOverview(
        WorkScheduleOverviewCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists retained dispatch occurrences for a readable schedule.
    /// </summary>
    Task<WorkScheduleOccurrenceQueryResult> ListOccurrences(
        WorkScheduleId scheduleId,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels future occurrences using the target work's cancel authorization.
    /// </summary>
    Task<WorkScheduleCancellationOutcome> Cancel(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default);
}

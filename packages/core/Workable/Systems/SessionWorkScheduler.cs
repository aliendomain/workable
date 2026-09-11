namespace Workable;

internal sealed class SessionWorkScheduler(
    WorkScheduler inner,
    WorkRequestContext requestContext) : IWorkScheduler
{
    public Task<WorkScheduleCreationOutcome> Create(
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default)
        => inner.Create(
            request,
            requestContext,
            cancellationToken);

    public Task<WorkScheduleCreationOutcome> Create<TInput>(
        string definitionName,
        TInput input,
        WorkScheduleTiming timing,
        WorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default)
        => this.Create(
            new WorkScheduleRequest(
                definitionName,
                timing,
                input is null ? null : WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
                workerOptions),
            cancellationToken);

    public Task<WorkScheduleSnapshot?> Get(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => inner.Get(scheduleId, cancellationToken);

    public Task<WorkScheduleQueryResult> List(
        WorkScheduleCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.List(criteria, cancellationToken);

    public Task<WorkScheduleOccurrenceQueryResult> ListOccurrences(
        WorkScheduleId scheduleId,
        int take = 100,
        CancellationToken cancellationToken = default)
        => inner.ListOccurrences(scheduleId, take, cancellationToken);

    public Task<WorkScheduleCancellationOutcome> Cancel(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => inner.Cancel(scheduleId, requestContext, cancellationToken);
}

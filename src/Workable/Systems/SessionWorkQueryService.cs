namespace Workable;

internal sealed class SessionWorkQueryService(
    IWorkQueryService inner,
    WorkRequestContext requestContext) : IWorkQueryService
{
    public WorkRequestContext RequestContext { get; } = requestContext;

    public Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => inner.Worker(workerId, cancellationToken);

    public Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
        => inner.WorkerIteration(iteration, cancellationToken);

    public Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.Workers(criteria, cancellationToken);

    public Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkerIterations(criteria, cancellationToken);

    public Task<WorkInfo?> WorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
        => inner.WorkInfo(definitionId, cancellationToken);

    public Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default)
        => inner.WorkInfo(name, cancellationToken);

    public Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkDefinitions(criteria, cancellationToken);

    public Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkerKeys(criteria, cancellationToken);

    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkerKeyTypes(criteria, cancellationToken);

    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkIterationKeys(criteria, cancellationToken);

    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkIterationKeyTypes(criteria, cancellationToken);

    public Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.WorkerStatusSummary(criteria, cancellationToken);

    public Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemDetails(criteria, cancellationToken);

    public Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
        => inner.SystemThroughput(criteria, throughput, cancellationToken);

    public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
        => inner.SystemThroughputSummary(criteria, throughput, cancellationToken);

    public Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemWorkerCounts(criteria, cancellationToken);

    public Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemIterationCounts(criteria, cancellationToken);

    public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemCommonKeyTypes(criteria, cancellationToken);

    public Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemFailedWorkers(criteria, cancellationToken);

    public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemFailedIterations(criteria, cancellationToken);

    public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemCompletedIterations(criteria, cancellationToken);
}

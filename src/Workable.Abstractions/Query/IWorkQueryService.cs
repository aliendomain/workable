namespace Workable;

public interface IWorkQueryService
{
    Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default);

    Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default);

    Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default);

    Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);
}

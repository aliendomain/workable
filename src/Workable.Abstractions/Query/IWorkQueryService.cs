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
        WorkDefinitionId definitionId,
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

    Task<WorkComponentQueryResult> Components(
        WorkComponentCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkComponentQueryResult> View(
        string name,
        WorkViewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemOverview> SystemOverview(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemThroughput> SystemThroughput(
        WorkOverviewCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemOverviewCounts> SystemOverviewCounts(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemFailedWorkersOverview> SystemFailedWorkers(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default);
}

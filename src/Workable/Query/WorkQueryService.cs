namespace Workable;

internal sealed class WorkQueryService(WorkerOperations operations) : IWorkQueryService
{
    public async Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => await operations.Query(WorkQueries.Worker(workerId), cancellationToken: cancellationToken);

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
        => await operations.Query(WorkQueries.WorkerIteration(iteration), cancellationToken: cancellationToken);

    public Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.Workers(criteria), cancellationToken: cancellationToken);

    public Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkerIterations(criteria), cancellationToken: cancellationToken);

    public async Task<WorkInfo?> WorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
        => await operations.Query(WorkQueries.WorkInfo(definitionId), cancellationToken: cancellationToken);

    public async Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default)
        => await operations.Query(WorkQueries.WorkInfo(name), cancellationToken: cancellationToken);

    public Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkDefinitions(criteria), cancellationToken: cancellationToken);

    public Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkerKeys(criteria), cancellationToken: cancellationToken);

    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkerKeyTypes(criteria), cancellationToken: cancellationToken);

    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkIterationKeys(criteria), cancellationToken: cancellationToken);

    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkIterationKeyTypes(criteria), cancellationToken: cancellationToken);

    public Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.WorkerStatusSummary(criteria), cancellationToken: cancellationToken);

    public Task<WorkComponentQueryResult> Components(
        WorkComponentCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.Components(criteria), scope, cancellationToken);

    public Task<WorkComponentQueryResult> View(
        string name,
        WorkViewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.View(name, criteria), scope, cancellationToken);

    public Task<WorkSystemOverview> SystemOverview(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemOverview(criteria), scope, cancellationToken);

    public Task<WorkSystemThroughput> SystemThroughput(
        WorkOverviewCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemThroughput(criteria, throughput), scope, cancellationToken);

    public Task<WorkSystemOverviewCounts> SystemOverviewCounts(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemOverviewCounts(criteria), scope, cancellationToken);

    public Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemWorkerCounts(criteria), scope, cancellationToken);

    public Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemIterationCounts(criteria), scope, cancellationToken);

    public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemCommonKeyTypes(criteria), scope, cancellationToken);

    public Task<WorkSystemFailedWorkersOverview> SystemFailedWorkers(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemFailedWorkers(criteria), scope, cancellationToken);

    public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemFailedIterations(criteria), scope, cancellationToken);

    public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkOverviewCriteria? criteria = null,
        WorkQueryScope? scope = null,
        CancellationToken cancellationToken = default)
        => operations.Query(WorkQueries.SystemCompletedIterations(criteria), scope, cancellationToken);
}

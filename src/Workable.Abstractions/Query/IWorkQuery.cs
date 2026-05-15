namespace Workable;

public interface IWorkQuery
{
    Task<WorkerSnapshot?> GetWorker(WorkerId workerId, CancellationToken cancellationToken = default);

    Task<WorkerIterationSnapshot?> GetWorkerIteration(WorkerIterationReference iteration, CancellationToken cancellationToken = default);

    Task<WorkerQueryResult> QueryWorkers(WorkerQuery query, CancellationToken cancellationToken = default);

    Task<WorkerIterationQueryResult> QueryWorkerIterations(WorkerIterationQuery query, CancellationToken cancellationToken = default);

    Task<WorkInfo?> GetWorkInfo(WorkDefinitionId definitionId, CancellationToken cancellationToken = default);

    Task<WorkInfo?> GetWorkInfo(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkDefinition>> QueryWorkDefinitions(WorkDefinitionQuery query, CancellationToken cancellationToken = default);

    Task<WorkerKeyQueryResult> QueryWorkerKeys(WorkerKeyQuery query, CancellationToken cancellationToken = default);

    Task<WorkerKeyTypeQueryResult> QueryWorkerKeyTypes(WorkerKeyTypeQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkIterationKeyQueryResult> QueryWorkIterationKeys(WorkIterationKeyQuery query, CancellationToken cancellationToken = default);

    Task<WorkIterationKeyTypeQueryResult> QueryWorkIterationKeyTypes(WorkIterationKeyTypeQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkerStatusSummary> GetWorkerStatusSummary(WorkerQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkComponentQueryResult> QueryComponents(WorkComponentQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkComponentQueryResult> GetView(string name, WorkViewQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkSystemOverview> GetSystemOverview(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkSystemThroughput> GetSystemOverviewThroughput(
        WorkOverviewQuery? query = null,
        WorkThroughputQuery? throughputQuery = null,
        CancellationToken cancellationToken = default);

    Task<WorkSystemOverviewCounts> GetSystemOverviewCounts(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkSystemWorkerCounts> GetSystemOverviewWorkerCounts(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkSystemIterationCounts> GetSystemOverviewIterationCounts(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkIterationKeyTypeFacet>> GetSystemOverviewCommonKeyTypes(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<WorkSystemFailedWorkersOverview> GetSystemOverviewFailedWorkers(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkerIterationOverviewItem>> GetSystemOverviewFailedIterations(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkerIterationOverviewItem>> GetSystemOverviewCompletedIterations(WorkOverviewQuery? query = null, CancellationToken cancellationToken = default);
}

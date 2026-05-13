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

    Task<WorkSystemOverview> GetSystemOverview(CancellationToken cancellationToken = default);

    Task<WorkSystemOverviewCounts> GetSystemOverviewCounts(CancellationToken cancellationToken = default);

    Task<WorkSystemWorkerCounts> GetSystemOverviewWorkerCounts(CancellationToken cancellationToken = default);

    Task<WorkSystemIterationCounts> GetSystemOverviewIterationCounts(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkIterationKeyTypeFacet>> GetSystemOverviewCommonKeyTypes(CancellationToken cancellationToken = default);

    Task<WorkSystemFailedWorkersOverview> GetSystemOverviewFailedWorkers(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkerIterationOverviewItem>> GetSystemOverviewFailedIterations(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkerIterationOverviewItem>> GetSystemOverviewCompletedIterations(CancellationToken cancellationToken = default);
}

namespace Workable;

public interface IWorkQuery
{
    Task<WorkerSnapshot?> GetWorker(WorkerId workerId, CancellationToken cancellationToken = default);

    Task<WorkerQueryResult> QueryWorkers(WorkerQuery query, CancellationToken cancellationToken = default);

    Task<WorkInfo?> GetWorkInfo(WorkDefinitionId definitionId, CancellationToken cancellationToken = default);

    Task<WorkInfo?> GetWorkInfo(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkDefinition>> QueryWorkDefinitions(WorkDefinitionQuery query, CancellationToken cancellationToken = default);

    Task<WorkerStatusSummary> GetWorkerStatusSummary(WorkerQuery? query = null, CancellationToken cancellationToken = default);
}

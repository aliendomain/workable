namespace Workable;

internal sealed record WorkerKeyTypesQueryDefinition(WorkerKeyTypeCriteria Criteria) :
    WorkQueryDefinition<WorkerKeyTypeQueryResult>("workerKeyTypes");

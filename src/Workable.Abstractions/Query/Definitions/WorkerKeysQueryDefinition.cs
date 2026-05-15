namespace Workable;

internal sealed record WorkerKeysQueryDefinition(WorkerKeyCriteria Criteria) :
    WorkQueryDefinition<WorkerKeyQueryResult>("workerKeys");

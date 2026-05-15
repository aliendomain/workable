namespace Workable;

internal sealed record WorkersQueryDefinition(WorkerCriteria Criteria) :
    WorkQueryDefinition<WorkerQueryResult>("workers");

namespace Workable;

internal sealed record WorkerIterationsQueryDefinition(WorkerIterationCriteria Criteria) :
    WorkQueryDefinition<WorkerIterationQueryResult>("workerIterations");

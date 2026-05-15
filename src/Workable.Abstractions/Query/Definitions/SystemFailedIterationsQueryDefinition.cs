namespace Workable;

internal sealed record SystemFailedIterationsQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkerIterationOverviewQueryResult>("systemFailedIterations");

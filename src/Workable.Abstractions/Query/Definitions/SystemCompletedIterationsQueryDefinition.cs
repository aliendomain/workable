namespace Workable;

internal sealed record SystemCompletedIterationsQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkerIterationOverviewQueryResult>("systemCompletedIterations");

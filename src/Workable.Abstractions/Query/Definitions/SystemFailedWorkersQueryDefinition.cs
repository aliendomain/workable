namespace Workable;

internal sealed record SystemFailedWorkersQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkSystemFailedWorkersOverview>("systemFailedWorkers");

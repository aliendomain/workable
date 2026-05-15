namespace Workable;

internal sealed record SystemWorkerCountsQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkSystemWorkerCounts>("systemWorkerCounts");

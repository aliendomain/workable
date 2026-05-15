namespace Workable;

internal sealed record SystemOverviewCountsQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkSystemOverviewCounts>("systemOverviewCounts");

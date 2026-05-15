namespace Workable;

internal sealed record SystemOverviewQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkSystemOverview>("systemOverview");

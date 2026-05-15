namespace Workable;

internal sealed record SystemIterationCountsQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkSystemIterationCounts>("systemIterationCounts");

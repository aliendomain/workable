namespace Workable;

internal sealed record SystemCommonKeyTypesQueryDefinition(WorkOverviewCriteria? Criteria) :
    WorkQueryDefinition<WorkIterationKeyTypeFacetQueryResult>("systemCommonKeyTypes");

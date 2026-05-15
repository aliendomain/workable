namespace Workable;

internal sealed record WorkIterationKeyTypesQueryDefinition(WorkIterationKeyTypeCriteria Criteria) :
    WorkQueryDefinition<WorkIterationKeyTypeQueryResult>("workIterationKeyTypes");

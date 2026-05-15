namespace Workable;

internal sealed record ComponentsQueryDefinition(WorkComponentCriteria Criteria) :
    WorkQueryDefinition<WorkComponentQueryResult>("components");

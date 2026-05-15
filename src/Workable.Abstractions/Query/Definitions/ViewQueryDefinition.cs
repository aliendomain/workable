namespace Workable;

internal sealed record ViewQueryDefinition(string ViewName, WorkViewCriteria Criteria) :
    WorkQueryDefinition<WorkComponentQueryResult>("view");

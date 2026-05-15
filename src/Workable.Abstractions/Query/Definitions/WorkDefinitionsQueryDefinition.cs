namespace Workable;

internal sealed record WorkDefinitionsQueryDefinition(WorkDefinitionCriteria Criteria) :
    WorkQueryDefinition<WorkDefinitionQueryResult>("workDefinitions");

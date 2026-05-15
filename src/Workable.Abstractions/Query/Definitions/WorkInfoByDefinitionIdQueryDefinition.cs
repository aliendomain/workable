namespace Workable;

internal sealed record WorkInfoByDefinitionIdQueryDefinition(WorkDefinitionId DefinitionId) :
    WorkQueryDefinition<WorkInfo>("workInfoByDefinitionId");

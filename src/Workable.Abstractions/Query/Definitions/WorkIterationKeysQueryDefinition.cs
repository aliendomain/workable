namespace Workable;

internal sealed record WorkIterationKeysQueryDefinition(WorkIterationKeyCriteria Criteria) :
    WorkQueryDefinition<WorkIterationKeyQueryResult>("workIterationKeys");

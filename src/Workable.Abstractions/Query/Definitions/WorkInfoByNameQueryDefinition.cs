namespace Workable;

internal sealed record WorkInfoByNameQueryDefinition(string WorkName) :
    WorkQueryDefinition<WorkInfo>("workInfoByName");

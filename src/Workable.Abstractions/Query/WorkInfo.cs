namespace Workable;

public sealed record WorkInfo(
    WorkDefinition Definition,
    WorkDefinitionStatus Status,
    WorkerRollup Workers);

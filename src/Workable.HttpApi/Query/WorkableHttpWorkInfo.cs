namespace Workable;

public sealed record WorkableHttpWorkInfo(
    WorkDefinition Definition,
    WorkDefinitionStatus Status,
    WorkerRollup Workers,
    WorkableHttpQueueRequestDescriptor QueueRequestSchema);

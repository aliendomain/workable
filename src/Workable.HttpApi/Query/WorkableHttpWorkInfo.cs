namespace Workable;

/// <summary>
/// Represents the HTTP work-info payload returned for a visible definition.
/// </summary>
/// <param name="Definition">The visible definition metadata and configuration.</param>
/// <param name="Status">The summary health status of the definition.</param>
/// <param name="Workers">The worker rollup for the definition.</param>
/// <param name="QueueRequestSchema">The queue-request schema metadata clients can use to render queue forms.</param>
public sealed record WorkableHttpWorkInfo(
    WorkDefinition Definition,
    WorkDefinitionStatus Status,
    WorkerRollup Workers,
    WorkableHttpQueueRequestDescriptor QueueRequestSchema);

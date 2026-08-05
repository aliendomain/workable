namespace Workable;

/// <summary>
/// Represents the HTTP worker-configuration payload used by configuration and queue-editing screens.
/// </summary>
/// <param name="ProfilingEnabled">Whether profiling is enabled in the worker's effective options.</param>
/// <param name="Configuration">The worker's effective runtime configuration.</param>
/// <param name="Input">The retained worker input payload, when one exists.</param>
/// <param name="SubjectId">The worker's primary subject identifier, when one exists.</param>
/// <param name="ConcurrencyKey">The worker's concurrency grouping key, when one exists.</param>
/// <param name="DefinitionInfo">The associated definition info payload, when the definition is visible to the caller.</param>
/// <param name="QueueRequestSchema">The queue-request schema metadata clients can use to render queue forms.</param>
public sealed record WorkableHttpWorkerConfiguration(
    bool ProfilingEnabled,
    WorkableHttpWorkConfiguration Configuration,
    WorkInput? Input,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    WorkInfo? DefinitionInfo,
    WorkableHttpQueueRequestDescriptor QueueRequestSchema)
{
    /// <summary>
    /// Gets whether automatic instrumentation is bounded or fully captured for this worker.
    /// </summary>
    public WorkProfileCaptureMode ProfilingCaptureMode { get; init; }
}

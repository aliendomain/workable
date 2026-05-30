namespace Workable;

public sealed record WorkableHttpWorkerConfiguration(
    bool ProfilingEnabled,
    WorkableHttpWorkConfiguration Configuration,
    WorkInput? Input,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    WorkInfo? DefinitionInfo,
    WorkableHttpQueueRequestDescriptor QueueRequestSchema);

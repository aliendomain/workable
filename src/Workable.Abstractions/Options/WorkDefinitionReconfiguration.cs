namespace Workable;

public sealed record WorkDefinitionReconfiguration(
    WorkerOptions? DefaultOptions = null,
    WorkConfiguration? Configuration = null);

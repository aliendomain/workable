namespace Workable;

/// <summary>
/// Describes definition-level configuration changes that apply to future workers.
/// </summary>
/// <param name="DefaultOptions">Optional replacement default worker options for future queued workers.</param>
/// <param name="Configuration">Optional replacement runtime configuration for future queued workers.</param>
public sealed record WorkDefinitionReconfiguration(
    WorkerOptions? DefaultOptions = null,
    WorkConfiguration? Configuration = null);

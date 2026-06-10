namespace Workable;

/// <summary>
/// Identifies which reconfiguration surface is currently being authorized.
/// </summary>
public enum WorkReconfigurationRequirementSurface
{
    /// <summary>
    /// The caller is reconfiguring an existing worker.
    /// </summary>
    Worker,

    /// <summary>
    /// The caller is reconfiguring a work definition for future workers.
    /// </summary>
    Definition,
}

/// <summary>
/// Provides common context for reconfiguration requirements.
/// </summary>
public record WorkReconfigurationRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkReconfigurationRequirementSurface Surface,
    WorkInput? RawInput = null,
    string? WorkerId = null,
    WorkWorkerReconfigurationChanges? WorkerChanges = null,
    WorkDefinitionReconfigurationChanges? DefinitionChanges = null);

/// <summary>
/// Provides common typed context for reconfiguration requirements.
/// </summary>
/// <typeparam name="TInput">The typed input value deserialized for the requirement.</typeparam>
public sealed record WorkReconfigurationRequirementContext<TInput>(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkReconfigurationRequirementSurface Surface,
    WorkInput? RawInput,
    TInput? Input,
    string? WorkerId = null,
    WorkWorkerReconfigurationChanges? WorkerChanges = null,
    WorkDefinitionReconfigurationChanges? DefinitionChanges = null) : WorkReconfigurationRequirementContext(
        Definition,
        RequestContext,
        Surface,
        RawInput,
        WorkerId,
        WorkerChanges,
        DefinitionChanges);

/// <summary>
/// Provides worker-reconfiguration-specific context for operate requirements.
/// </summary>
public record WorkWorkerReconfigurationRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    string WorkerId,
    WorkInput? RawInput,
    WorkWorkerReconfigurationChanges Changes);

/// <summary>
/// Provides typed worker-reconfiguration-specific context for operate requirements.
/// </summary>
/// <typeparam name="TInput">The typed input value deserialized for the requirement.</typeparam>
public sealed record WorkWorkerReconfigurationRequirementContext<TInput>(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    string WorkerId,
    WorkInput? RawInput,
    WorkWorkerReconfigurationChanges Changes,
    TInput? Input) : WorkWorkerReconfigurationRequirementContext(
        Definition,
        RequestContext,
        WorkerId,
        RawInput,
        Changes);

/// <summary>
/// Provides definition-reconfiguration-specific context for operate requirements.
/// </summary>
public record WorkDefinitionReconfigurationRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkDefinitionReconfigurationChanges Changes);

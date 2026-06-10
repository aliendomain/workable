namespace Workable;

/// <summary>
/// Identifies the operate surface currently being authorized.
/// </summary>
public enum WorkOperateRequirementSurface
{
    /// <summary>
    /// The caller is queueing a new worker.
    /// </summary>
    Queueing,

    /// <summary>
    /// The caller is applying an action to an existing worker.
    /// </summary>
    WorkerAction,

    /// <summary>
    /// The caller is reconfiguring an existing worker.
    /// </summary>
    WorkerReconfiguration,

    /// <summary>
    /// The caller is reconfiguring a work definition for future workers.
    /// </summary>
    DefinitionReconfiguration,
}

/// <summary>
/// Identifies the worker action currently being authorized by an operate requirement.
/// </summary>
public enum WorkOperateAction
{
    /// <summary>
    /// Starts a worker.
    /// </summary>
    Start,

    /// <summary>
    /// Pauses a worker.
    /// </summary>
    Pause,

    /// <summary>
    /// Cancels a worker.
    /// </summary>
    Cancel,

    /// <summary>
    /// Pushes a waiting worker.
    /// </summary>
    Push,

    /// <summary>
    /// Purges a final worker.
    /// </summary>
    Purge,
}

/// <summary>
/// Provides common context for operate requirements that can apply to queueing, worker actions, and reconfiguration.
/// </summary>
public record WorkOperateRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkOperateRequirementSurface Surface,
    WorkInput? RawInput,
    WorkOperateAction? Action = null,
    string? WorkerId = null,
    WorkWorkerReconfigurationChanges? WorkerChanges = null,
    WorkDefinitionReconfigurationChanges? DefinitionChanges = null);

/// <summary>
/// Provides common typed context for operate requirements that can apply to queueing, worker actions, and reconfiguration.
/// </summary>
/// <typeparam name="TInput">The typed input value deserialized for the requirement.</typeparam>
public sealed record WorkOperateRequirementContext<TInput>(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkOperateRequirementSurface Surface,
    WorkInput? RawInput,
    TInput? Input,
    WorkOperateAction? Action = null,
    string? WorkerId = null,
    WorkWorkerReconfigurationChanges? WorkerChanges = null,
    WorkDefinitionReconfigurationChanges? DefinitionChanges = null) : WorkOperateRequirementContext(
        Definition,
        RequestContext,
        Surface,
        RawInput,
        Action,
        WorkerId,
        WorkerChanges,
        DefinitionChanges);

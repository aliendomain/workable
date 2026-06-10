namespace Workable;

/// <summary>
/// Provides queue-specific context for operate requirements.
/// </summary>
public record WorkQueueRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkInput? RawInput,
    WorkerOptions? Options);

/// <summary>
/// Provides typed queue-specific context for operate requirements.
/// </summary>
/// <typeparam name="TInput">The typed input value deserialized for the requirement.</typeparam>
public sealed record WorkQueueRequirementContext<TInput>(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkInput? RawInput,
    WorkerOptions? Options,
    TInput? Input) : WorkQueueRequirementContext(
        Definition,
        RequestContext,
        RawInput,
        Options);

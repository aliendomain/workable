namespace Workable;

/// <summary>
/// Provides worker-action-specific context for operate requirements.
/// </summary>
public record WorkWorkerActionRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    string WorkerId,
    WorkInput? RawInput,
    WorkOperateAction Action)
{
}

/// <summary>
/// Provides typed worker-action-specific context for operate requirements.
/// </summary>
/// <typeparam name="TInput">The typed input value deserialized for the requirement.</typeparam>
public sealed record WorkWorkerActionRequirementContext<TInput>(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    string WorkerId,
    WorkInput? RawInput,
    WorkOperateAction Action,
    TInput? Input) : WorkWorkerActionRequirementContext(
        Definition,
        RequestContext,
        WorkerId,
        RawInput,
        Action);

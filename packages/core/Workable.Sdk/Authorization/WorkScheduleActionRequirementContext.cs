namespace Workable;

/// <summary>
/// Provides runtime-schedule-action-specific context for operate requirements.
/// </summary>
public record WorkScheduleActionRequirementContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    string ScheduleId,
    WorkInput? RawInput,
    WorkOperateAction Action);

/// <summary>
/// Provides typed runtime-schedule-action-specific context for operate requirements.
/// </summary>
/// <typeparam name="TInput">The typed retained schedule input deserialized for the requirement.</typeparam>
public sealed record WorkScheduleActionRequirementContext<TInput>(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    string ScheduleId,
    WorkInput? RawInput,
    WorkOperateAction Action,
    TInput? Input) : WorkScheduleActionRequirementContext(
        Definition,
        RequestContext,
        ScheduleId,
        RawInput,
        Action);

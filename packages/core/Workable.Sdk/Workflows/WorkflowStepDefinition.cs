namespace Workable;

/// <summary>
/// Identifies the built-in step kinds supported by the workflow definition surface.
/// </summary>
public enum WorkflowStepKind
{
    /// <summary>
    /// A step that queues one existing Workable work definition.
    /// </summary>
    DispatchWork,

    /// <summary>
    /// A step that expands an earlier output array into one child work item per element.
    /// </summary>
    DispatchEach,

    /// <summary>
    /// A step that contains parallel child steps.
    /// </summary>
    Parallel,

    /// <summary>
    /// A synchronization barrier that waits for prior parallel work to settle.
    /// </summary>
    Join,
}

/// <summary>
/// Represents one step in a workflow definition.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
/// <param name="Kind">The kind of workflow step.</param>
public abstract record WorkflowStepDefinition(
    string Name,
    WorkflowStepKind Kind);

/// <summary>
/// Represents a step that queues one existing Workable work definition.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
/// <param name="WorkDefinition">The target Workable work definition.</param>
/// <param name="Input">Optional static input payload supplied when the step queues work.</param>
public sealed record DispatchWorkflowStepDefinition(
    string Name,
    WorkDefinition WorkDefinition,
    WorkInput? Input = null)
    : WorkflowStepDefinition(Name, WorkflowStepKind.DispatchWork);

/// <summary>
/// Represents a step that expands an earlier output array into multiple child work dispatches.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
/// <param name="SourceStep">The earlier workflow step whose completed output should be expanded.</param>
/// <param name="WorkDefinition">The target Workable work definition.</param>
/// <param name="SourceSelector">The generated selector that chooses the array within the source output. When its JSON pointer is omitted, the root output value must be an array.</param>
public sealed record DispatchEachWorkflowStepDefinition(
    string Name,
    WorkflowStepReference SourceStep,
    WorkDefinition WorkDefinition,
    WorkflowOutputSelector SourceSelector)
    : WorkflowStepDefinition(Name, WorkflowStepKind.DispatchEach);

/// <summary>
/// Represents a step that contains parallel child steps.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
/// <param name="Steps">The child steps that should be dispatched in parallel.</param>
public sealed record ParallelWorkflowStepDefinition(
    string Name,
    IReadOnlyList<WorkflowStepDefinition> Steps)
    : WorkflowStepDefinition(Name, WorkflowStepKind.Parallel);

/// <summary>
/// Represents a synchronization barrier that waits for prior parallel branches to settle.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
public sealed record JoinWorkflowStepDefinition(string Name)
    : WorkflowStepDefinition(Name, WorkflowStepKind.Join);

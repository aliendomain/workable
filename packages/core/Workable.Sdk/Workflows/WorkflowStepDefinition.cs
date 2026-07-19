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
    /// A named sequential branch inside a structural workflow step.
    /// </summary>
    Branch,

    /// <summary>
    /// A synchronization barrier that waits for prior parallel work to settle.
    /// </summary>
    Join,
}

/// <summary>
/// Identifies where a dispatch step should get its child work input.
/// </summary>
public enum WorkflowDispatchInputSource
{
    /// <summary>
    /// The dispatch step uses the static input configured on the workflow definition.
    /// </summary>
    Static,

    /// <summary>
    /// The dispatch step uses the input supplied when the workflow run was started.
    /// </summary>
    WorkflowInput,
}

/// <summary>
/// Describes how a workflow should react when a worker dispatched by a <c>DispatchEach</c> step is canceled.
/// </summary>
public enum WorkflowCanceledChildBehavior
{
    /// <summary>
    /// Treats the canceled child as skipped and allows the workflow to continue after the remaining children settle.
    /// </summary>
    Continue = 0,

    /// <summary>
    /// Blocks the workflow at its next synchronization point without canceling the remaining sibling workers.
    /// </summary>
    Block = 1,

    /// <summary>
    /// Cancels the workflow and its remaining outstanding child workers.
    /// </summary>
    CancelWorkflow = 2,
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
/// <param name="InputSource">Identifies whether the step uses static input or the workflow run input.</param>
public sealed record DispatchWorkflowStepDefinition(
    string Name,
    WorkDefinition WorkDefinition,
    WorkInput? Input = null,
    WorkflowDispatchInputSource InputSource = WorkflowDispatchInputSource.Static)
    : WorkflowStepDefinition(Name, WorkflowStepKind.DispatchWork);

/// <summary>
/// Represents a step that expands an earlier output array into multiple child work dispatches.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
/// <param name="SourceStep">The earlier workflow step whose completed output should be expanded.</param>
/// <param name="WorkDefinition">The target Workable work definition.</param>
/// <param name="SourceSelector">The generated selector that chooses the array within the source output. When its JSON pointer is omitted, the root output value must be an array.</param>
/// <param name="CanceledChildBehavior">The behavior to apply when one of the expanded child workers is canceled.</param>
public sealed record DispatchEachWorkflowStepDefinition(
    string Name,
    WorkflowStepReference SourceStep,
    WorkDefinition WorkDefinition,
    WorkflowOutputSelector SourceSelector,
    WorkflowCanceledChildBehavior CanceledChildBehavior = WorkflowCanceledChildBehavior.Continue)
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
/// Represents a named sequential branch inside a workflow structure node.
/// </summary>
/// <param name="Name">The stable workflow-local branch name.</param>
/// <param name="Steps">The sequential child steps in this branch.</param>
public sealed record BranchWorkflowStepDefinition(
    string Name,
    IReadOnlyList<WorkflowStepDefinition> Steps)
    : WorkflowStepDefinition(Name, WorkflowStepKind.Branch);

/// <summary>
/// Represents a synchronization barrier that waits for prior parallel branches to settle.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
public sealed record JoinWorkflowStepDefinition(string Name)
    : WorkflowStepDefinition(Name, WorkflowStepKind.Join);

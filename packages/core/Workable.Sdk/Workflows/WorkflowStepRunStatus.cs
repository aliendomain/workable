namespace Workable;

/// <summary>
/// Describes the lifecycle status of one workflow step.
/// </summary>
public enum WorkflowStepRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

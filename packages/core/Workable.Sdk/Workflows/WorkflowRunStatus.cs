namespace Workable;

/// <summary>
/// Describes the lifecycle status of a workflow run.
/// </summary>
public enum WorkflowRunStatus
{
    Running,
    Completed,
    Failed,
    Canceled,
    Invalid,
    NotFound,
    Unauthorized,
}

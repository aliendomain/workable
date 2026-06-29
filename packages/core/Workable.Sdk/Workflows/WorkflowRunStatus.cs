namespace Workable;

/// <summary>
/// Describes the lifecycle status of a workflow run.
/// </summary>
public enum WorkflowRunStatus
{
    Running,
    Paused,
    Blocked,
    Completed,
    Failed,
    Canceled,
    Invalid,
    NotFound,
    Unauthorized,
}

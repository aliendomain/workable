namespace Workable;

/// <summary>
/// Describes the lifecycle actions currently available for one workflow run.
/// </summary>
public sealed record WorkflowAvailableActions(
    bool Start,
    bool Pause,
    bool Cancel)
{
    internal static WorkflowAvailableActions For(WorkflowRunStatus status)
        => status switch
        {
            WorkflowRunStatus.Running => new WorkflowAvailableActions(
                Start: false,
                Pause: true,
                Cancel: true),
            WorkflowRunStatus.Paused or WorkflowRunStatus.Blocked => new WorkflowAvailableActions(
                Start: true,
                Pause: false,
                Cancel: true),
            _ => new WorkflowAvailableActions(
                Start: false,
                Pause: false,
                Cancel: false),
        };
}

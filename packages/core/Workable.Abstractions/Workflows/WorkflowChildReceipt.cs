namespace Workable;

/// <summary>
/// Stores the retained final receipt for one workflow child worker.
/// </summary>
public sealed record WorkflowChildReceipt(
    WorkerId WorkerId,
    string StepName,
    string DefinitionName,
    WorkerState State,
    DateTimeOffset CompletedAt,
    IReadOnlyList<WorkMessage> Messages,
    WorkOutput? Output)
{
    /// <summary>
    /// Gets the completion status implied by the retained worker state.
    /// </summary>
    public WorkCompletionStatus CompletionStatus => this.State switch
    {
        WorkerState.Completed => WorkCompletionStatus.Completed,
        WorkerState.Failed => WorkCompletionStatus.Failed,
        WorkerState.Paused => WorkCompletionStatus.Paused,
        WorkerState.Interrupted => WorkCompletionStatus.Interrupted,
        WorkerState.Canceled => WorkCompletionStatus.Canceled,
        _ => WorkCompletionStatus.Invalid,
    };
}

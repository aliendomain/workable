namespace Workable;

internal sealed class WorkflowRunHandle : IWorkflowRunHandle
{
    private readonly Task<WorkflowRunCompletion>? completion;

    private WorkflowRunHandle(WorkflowStartOutcome startOutcome, Task<WorkflowRunCompletion>? completion = null)
    {
        this.StartOutcome = startOutcome;
        this.completion = completion;
    }

    public WorkflowStartOutcome StartOutcome { get; }

    public WorkflowRunId? RunId => this.StartOutcome.RunId;

    public static WorkflowRunHandle Accepted(WorkflowStartOutcome startOutcome, Task<WorkflowRunCompletion> completion)
        => new(startOutcome, completion);

    public static WorkflowRunHandle Rejected(WorkflowStartOutcome startOutcome)
        => new(startOutcome);

    public Task<WorkflowRunCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.completion ?? Task.FromResult(new WorkflowRunCompletion(
            this.StartOutcome.Status switch
            {
                WorkflowStartStatus.NotFound => WorkflowRunStatus.NotFound,
                WorkflowStartStatus.Unauthorized => WorkflowRunStatus.Unauthorized,
                _ => WorkflowRunStatus.Invalid,
            },
            null,
            this.StartOutcome.Messages));
    }
}

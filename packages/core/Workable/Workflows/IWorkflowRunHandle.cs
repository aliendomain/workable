namespace Workable;

internal interface IWorkflowRunHandle
{
    WorkflowStartOutcome StartOutcome { get; }

    WorkflowRunId? RunId { get; }

    Task<WorkflowRunCompletion> WaitForCompletion(CancellationToken cancellationToken = default);
}

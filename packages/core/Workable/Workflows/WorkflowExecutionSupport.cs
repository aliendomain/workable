namespace Workable;

internal static class WorkflowExecutionSupport
{
    public static async Task<WorkflowRunCompletion> WaitForOutstanding(
        IReadOnlyList<(string StepName, IWorkerHandle Handle)> outstanding,
        CancellationToken cancellationToken)
    {
        if (outstanding.Count == 0)
        {
            return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
        }

        var completions = await Task.WhenAll(outstanding.Select(item => item.Handle.WaitForCompletion(cancellationToken)));
        var failure = completions.FirstOrDefault(completion => !completion.IsCompletedSuccessfully);
        return failure is null
            ? new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, [])
            : new WorkflowRunCompletion(ToWorkflowStatus(failure.Status), null, failure.Messages);
    }

    public static async Task<WorkflowRunCompletion> WaitForOutstanding(
        IReadOnlyList<WorkerId> workerIds,
        Func<WorkerId, IWorkerHandle> createWorkerHandle,
        CancellationToken cancellationToken)
    {
        if (workerIds.Count == 0)
        {
            return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
        }

        var outstanding = workerIds
            .Distinct()
            .Select(workerId => (workerId.ToString(), createWorkerHandle(workerId)))
            .ToArray();
        return await WaitForOutstanding(outstanding, cancellationToken);
    }

    public static WorkflowRunStatus ToWorkflowStatus(WorkCompletionStatus status)
        => status switch
        {
            WorkCompletionStatus.Completed => WorkflowRunStatus.Completed,
            WorkCompletionStatus.Canceled => WorkflowRunStatus.Canceled,
            WorkCompletionStatus.Failed => WorkflowRunStatus.Failed,
            WorkCompletionStatus.Interrupted => WorkflowRunStatus.Failed,
            WorkCompletionStatus.NotFound => WorkflowRunStatus.NotFound,
            WorkCompletionStatus.Invalid => WorkflowRunStatus.Invalid,
            _ => WorkflowRunStatus.Invalid,
        };

    public static WorkInput AddWorkflowIdentifiers(
        WorkInput? input,
        WorkflowRunId runId,
        string workflowDefinitionName,
        string stepName)
        => (input ?? WorkInput.Empty)
            .WithIdentifier(new WorkIdentifier("workflow-run", runId.ToString()))
            .WithIdentifier(new WorkIdentifier("workflow-definition", workflowDefinitionName))
            .WithIdentifier(new WorkIdentifier("workflow-step", stepName));

    public static async Task CancelOutstandingChildren(
        WorkflowRunState run,
        IWorkSystemSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(session);

        foreach (var workerId in run.GetOutstandingWorkerIds().Distinct())
        {
            var snapshot = await session.Query.Worker(workerId, cancellationToken);
            if (snapshot is null || snapshot.IsFinal || snapshot.State == WorkerState.Failed)
            {
                continue;
            }

            await session.Workers.Execute(snapshot.Version, WorkAction.Cancel, cancellationToken);
        }
    }
}

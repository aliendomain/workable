namespace Workable;

internal static class WorkflowExecutionSupport
{
    private static readonly TimeSpan WorkerControlPollInterval = TimeSpan.FromMilliseconds(25);

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
            WorkCompletionStatus.Failed => WorkflowRunStatus.Blocked,
            WorkCompletionStatus.Paused => WorkflowRunStatus.Blocked,
            WorkCompletionStatus.Canceled => WorkflowRunStatus.Blocked,
            WorkCompletionStatus.Interrupted => WorkflowRunStatus.Blocked,
            WorkCompletionStatus.NotFound => WorkflowRunStatus.Failed,
            WorkCompletionStatus.Invalid => WorkflowRunStatus.Failed,
            _ => WorkflowRunStatus.Failed,
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

    public static WorkCompletion FromReceipt(WorkflowChildReceipt receipt)
        => new(
            receipt.CompletionStatus,
            Worker: null,
            receipt.Output,
            receipt.Messages);

    public static async Task CancelOutstandingChildren(
        WorkflowRunState run,
        IWorkSystemSession session,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(session);

        foreach (var workerId in run.GetOutstandingWorkerIds().Distinct())
        {
            var snapshot = await GetSettledWorkerSnapshot(workerId, session, getAuthoritativeWorker, cancellationToken);
            if (snapshot is null || snapshot.IsFinal)
            {
                continue;
            }

            await session.Workers.Execute(snapshot.Version, WorkAction.Cancel, cancellationToken);
        }
    }

    public static async Task PauseOutstandingChildren(
        WorkflowRunState run,
        IWorkSystemSession session,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(session);

        foreach (var workerId in run.GetOutstandingWorkerIds().Distinct())
        {
            var snapshot = await GetSettledWorkerSnapshot(workerId, session, getAuthoritativeWorker, cancellationToken);
            if (snapshot is null || snapshot.IsFinal || snapshot.State == WorkerState.Paused)
            {
                continue;
            }

            if (snapshot.State is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying)
            {
                await session.Workers.Execute(snapshot.Version, WorkAction.Pause, cancellationToken);
            }
        }
    }

    public static async Task ResumeOutstandingChildren(
        WorkflowRunState run,
        IWorkSystemSession session,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(session);

        foreach (var workerId in run.GetOutstandingWorkerIds().Distinct())
        {
            while (true)
            {
                var snapshot = await GetSettledWorkerSnapshot(workerId, session, getAuthoritativeWorker, cancellationToken);
                if (snapshot is null || snapshot.IsFinal || snapshot.State is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying)
                {
                    break;
                }

                if (snapshot.State is not (WorkerState.Paused or WorkerState.Queued))
                {
                    break;
                }

                var outcome = await session.Workers.Execute(snapshot.Version, WorkAction.Start, cancellationToken);
                if (outcome.IsAccepted || outcome.Worker?.State is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Completed)
                {
                    break;
                }

                if (outcome.Worker?.State is not (WorkerState.Paused or WorkerState.Queued))
                {
                    break;
                }

                await Task.Delay(WorkerControlPollInterval, cancellationToken);
            }
        }
    }

    private static async Task<WorkerSnapshot?> GetSettledWorkerSnapshot(
        WorkerId workerId,
        IWorkSystemSession session,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = getAuthoritativeWorker is not null
                ? await getAuthoritativeWorker(workerId, cancellationToken)
                : await session.Query.Worker(workerId, cancellationToken);
            if (snapshot is null || !WorkerStateMachine.IsTransitioning(snapshot.State))
            {
                return snapshot;
            }

            await Task.Delay(WorkerControlPollInterval, cancellationToken);
        }
    }
}

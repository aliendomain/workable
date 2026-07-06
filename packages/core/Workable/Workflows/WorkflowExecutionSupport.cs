using System.Text.Json;

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

    public static WorkInput? ResolveDispatchInput(
        DispatchWorkflowStepDefinition step,
        WorkflowRunState run)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(run);

        return step.InputSource == WorkflowDispatchInputSource.WorkflowInput
            ? run.Input
            : step.Input;
    }

    public static WorkCompletion FromReceipt(WorkflowChildReceipt receipt)
        => new(
            receipt.CompletionStatus,
            Worker: null,
            receipt.Output,
            receipt.Messages);

    public static (IReadOnlyList<WorkInput> Inputs, IReadOnlyList<WorkMessage> Messages) CreateDispatchEachInputs(
        DispatchEachWorkflowStepDefinition step,
        IReadOnlyList<WorkOutput?> outputs)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(outputs);

        var inputs = new List<WorkInput>();
        foreach (var output in outputs)
        {
            if (!TryResolveDispatchEachArray(step, output, out var items, out var message))
            {
                return ([], [message]);
            }

            inputs.AddRange(items.Select(static item => WorkInput.FromJson(item.GetRawText())));
        }

        return (inputs, []);
    }

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

    private static bool TryResolveDispatchEachArray(
        DispatchEachWorkflowStepDefinition step,
        WorkOutput? output,
        out IReadOnlyList<JsonElement> items,
        out WorkMessage message)
    {
        if (string.IsNullOrWhiteSpace(output?.Json))
        {
            items = [];
            message = WorkMessage.Error(
                "workable.workflow.dispatch_each.source_output_required",
                $"Workflow step '{step.Name}' could not expand source step '{step.SourceStep.StepName}' because the source output was empty.",
                "workflow.dispatch_each");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output.Json);
            if (!TryResolveJsonPointer(document.RootElement, step.SourceSelector.JsonPointer, out var resolved))
            {
                items = [];
                message = WorkMessage.Error(
                    "workable.workflow.dispatch_each.source_pointer_not_found",
                    $"Workflow step '{step.Name}' could not resolve JSON pointer '{step.SourceSelector.JsonPointer}' on source step '{step.SourceStep.StepName}'.",
                    "workflow.dispatch_each");
                return false;
            }

            if (resolved.ValueKind != JsonValueKind.Array)
            {
                items = [];
                message = WorkMessage.Error(
                    "workable.workflow.dispatch_each.source_output_not_array",
                    $"Workflow step '{step.Name}' expected source step '{step.SourceStep.StepName}' to resolve to a JSON array.",
                    "workflow.dispatch_each");
                return false;
            }

            items = resolved.EnumerateArray().Select(static item => item.Clone()).ToArray();
            message = default!;
            return true;
        }
        catch (JsonException exception)
        {
            items = [];
            message = WorkMessage.Error(
                "workable.workflow.dispatch_each.source_output_invalid_json",
                $"Workflow step '{step.Name}' could not parse the source output from step '{step.SourceStep.StepName}': {exception.Message}",
                "workflow.dispatch_each");
            return false;
        }
    }

    private static bool TryResolveJsonPointer(
        JsonElement root,
        string? pointer,
        out JsonElement resolved)
    {
        if (string.IsNullOrWhiteSpace(pointer))
        {
            resolved = root;
            return true;
        }

        if (!pointer.StartsWith("/", StringComparison.Ordinal))
        {
            resolved = default;
            return false;
        }

        var current = root;
        foreach (var segment in pointer.Split('/').Skip(1).Select(rawSegment => rawSegment
                     .Replace("~1", "/", StringComparison.Ordinal)
                     .Replace("~0", "~", StringComparison.Ordinal)))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    resolved = default;
                    return false;
                }

                continue;
            }

            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0)
            {
                var currentIndex = 0;
                foreach (var item in current.EnumerateArray())
                {
                    if (currentIndex == index)
                    {
                        current = item;
                        goto NextSegment;
                    }

                    currentIndex++;
                }
            }

            resolved = default;
            return false;

        NextSegment:
            continue;
        }

        resolved = current;
        return true;
    }
}

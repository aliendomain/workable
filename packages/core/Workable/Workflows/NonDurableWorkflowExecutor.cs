namespace Workable;

internal sealed class NonDurableWorkflowExecutor(
    Func<WorkRequestContext, IWorkSystemSession> createSession,
    Func<WorkerId, IWorkerHandle>? createWorkerHandle = null,
    WorkflowEventPublisher? workflowEvents = null,
    Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker = null)
{
    public Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
        => this.Execute(run, workflow, wasPaused: false, null, null, cancellationToken);

    public async Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        bool wasPaused,
        Func<bool>? isPauseRequested = null,
        Func<bool>? isCancelRequested = null,
        CancellationToken cancellationToken = default)
    {
        var workerHandleFactory = createWorkerHandle
            ?? (_ => throw new InvalidOperationException("A worker-handle factory is required to wait on non-durable workflow children."));
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        isPauseRequested ??= static () => false;
        isCancelRequested ??= static () => false;
        var activeHandles = new System.Collections.Concurrent.ConcurrentDictionary<WorkerId, IWorkerHandle>();

        try
        {
            run.MarkRunning();
            var session = createSession(run.RequestContext);
            if (wasPaused)
            {
                await WorkflowExecutionSupport.ResumeOutstandingChildren(run, session, getAuthoritativeWorker, cancellationToken);
            }

            foreach (var step in workflow.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var status = run.GetStepStatus(step.Name);
                if (status == WorkflowStepRunStatus.Completed)
                {
                    continue;
                }

                var stepCompletion = await this.ExecuteStep(
                    run,
                    workflow,
                    session,
                    step,
                    activeHandles,
                    workerHandleFactory,
                    scopedJoinWorkerIds: null,
                    cancellationToken);
                if (stepCompletion is not null)
                {
                    return stepCompletion;
                }
            }

            if (run.GetOutstandingWorkerIds().Count > 0)
            {
                var completion = await WorkflowExecutionSupport.WaitForOutstanding(
                    run.GetOutstandingWorkerIds(),
                    workerId => activeHandles.TryGetValue(workerId, out var handle)
                        ? handle
                        : workerHandleFactory(workerId),
                    run,
                    workflow,
                    cancellationToken);
                if (!completion.IsCompletedSuccessfully)
                {
                    return CompleteFromChildOutcome(run, completion);
                }
            }

            return run.Complete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isCancelRequested() || !isPauseRequested())
            {
                return run.Cancel();
            }

            var session = createSession(run.RequestContext);
            await WorkflowExecutionSupport.PauseOutstandingChildren(run, session, getAuthoritativeWorker, CancellationToken.None);
            return run.Pause();
        }
        catch (Exception exception) when (ShouldHandleExecutionException(exception))
        {
            return run.Fail(
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")]);
        }
    }

    private static bool ShouldHandleExecutionException(Exception exception)
        => exception is not (
            OperationCanceledException or
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            ThreadAbortException or
            InvalidProgramException);

    private async Task<WorkflowRunCompletion?> ExecuteStep(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        IWorkSystemSession session,
        WorkflowStepDefinition step,
        System.Collections.Concurrent.ConcurrentDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        IReadOnlyList<WorkerId>? scopedJoinWorkerIds,
        CancellationToken cancellationToken)
    {
        var status = run.GetStepStatus(step.Name);
        if (status == WorkflowStepRunStatus.Completed)
        {
            return null;
        }

        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        switch (step)
        {
            case DispatchWorkflowStepDefinition dispatch:
                {
                    var result = await this.Dispatch(run, session, dispatch, cancellationToken);
                    if (!result.IsAccepted)
                    {
                        return run.Fail(result.Messages);
                    }

                    if (result.Handle is not null && result.Handle.WorkerId is { } workerId)
                    {
                        activeHandles[workerId] = result.Handle;
                    }

                    return null;
                }
            case DispatchEachWorkflowStepDefinition dispatchEach:
                {
                    var result = await this.DispatchEach(
                        run,
                        workflow,
                        session,
                        dispatchEach,
                        activeHandles,
                        workerHandleFactory,
                        cancellationToken);
                    if (!result.IsAccepted)
                    {
                        if (result.FailureStatus == WorkflowRunStatus.Blocked)
                        {
                            publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                            return run.Block(result.Messages);
                        }

                        if (result.FailureStatus == WorkflowRunStatus.Canceled)
                        {
                            publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                            return CompleteFromChildOutcome(
                                run,
                                new WorkflowRunCompletion(result.FailureStatus, null, result.Messages));
                        }

                        run.FailStep(dispatchEach.Name, result.Messages);
                        publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                        return run.Fail(result.Messages);
                    }

                    foreach (var handle in result.Handles.Where(handle => handle.WorkerId is not null))
                    {
                        activeHandles[handle.WorkerId!.Value] = handle;
                    }

                    return null;
                }
            case ParallelWorkflowStepDefinition parallel:
                return await this.ExecuteParallel(run, workflow, session, parallel, activeHandles, workerHandleFactory, cancellationToken);
            case BranchWorkflowStepDefinition branch:
                return await this.ExecuteBranch(run, workflow, session, branch, activeHandles, workerHandleFactory, cancellationToken);
            case JoinWorkflowStepDefinition join:
                {
                    if (status == WorkflowStepRunStatus.Pending)
                    {
                        run.MarkStepRunning(join.Name, scopedJoinWorkerIds ?? run.GetOutstandingWorkerIds());
                        publisher.StepUpdated(run.ToSnapshot(), join.Name);
                    }

                    var completion = await this.WaitForJoinOutstanding(
                        run,
                        workflow,
                        join.Name,
                        activeHandles,
                        workerHandleFactory,
                        scopedJoinWorkerIds,
                        cancellationToken);
                    if (!completion.IsCompletedSuccessfully)
                    {
                        if (completion.Status == WorkflowRunStatus.Failed)
                        {
                            run.FailStep(join.Name, completion.Messages);
                        }

                        publisher.StepUpdated(run.ToSnapshot(), join.Name);
                        return CompleteFromChildOutcome(run, completion);
                    }

                    run.MarkStepCompleted(join.Name);
                    publisher.StepUpdated(run.ToSnapshot(), join.Name);
                    return null;
                }
            default:
                return run.Fail(
                    [WorkMessage.Error(
                        "workable.workflow.step.unsupported",
                        $"Workflow step '{step.Name}' uses unsupported kind '{step.Kind}'.",
                        "workflow.step")]);
        }
    }

    private async Task<WorkflowRunCompletion?> ExecuteParallel(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        IWorkSystemSession session,
        ParallelWorkflowStepDefinition step,
        System.Collections.Concurrent.ConcurrentDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        CancellationToken cancellationToken)
    {
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        run.MarkStepRunning(step.Name);
        publisher.StepUpdated(run.ToSnapshot(), step.Name);

        var childTasks = step.Steps
            .Select(child => this.ExecuteStep(
                run,
                workflow,
                session,
                child,
                activeHandles,
                workerHandleFactory,
                scopedJoinWorkerIds: null,
                cancellationToken))
            .ToArray();
        var childCompletions = await Task.WhenAll(childTasks);
        foreach (var completion in childCompletions.Where(static completion => completion is not null))
        {
            if (completion!.Status != WorkflowRunStatus.Canceled)
            {
                run.FailStep(step.Name, completion.Messages);
            }
            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return completion;
        }

        run.MarkStepCompleted(step.Name, CollectStepWorkerIds(run, step.Steps));
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        return null;
    }

    private async Task<WorkflowRunCompletion?> ExecuteBranch(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        IWorkSystemSession session,
        BranchWorkflowStepDefinition step,
        System.Collections.Concurrent.ConcurrentDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        CancellationToken cancellationToken)
    {
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        run.MarkStepRunning(step.Name);
        publisher.StepUpdated(run.ToSnapshot(), step.Name);

        foreach (var child in step.Steps)
        {
            var scopedJoinWorkerIds = child is JoinWorkflowStepDefinition
                ? GetOutstandingWorkerIdsBeforeStep(run, step.Steps, child.Name)
                : null;
            var completion = await this.ExecuteStep(
                run,
                workflow,
                session,
                child,
                activeHandles,
                workerHandleFactory,
                scopedJoinWorkerIds,
                cancellationToken);
            if (completion is not null)
            {
                if (completion.Status != WorkflowRunStatus.Canceled)
                {
                    run.FailStep(step.Name, completion.Messages);
                }
                publisher.StepUpdated(run.ToSnapshot(), step.Name);
                return completion;
            }
        }

        run.MarkStepCompleted(step.Name, CollectStepWorkerIds(run, step.Steps));
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        return null;
    }

    private static IReadOnlyList<WorkerId> CollectStepWorkerIds(
        WorkflowRunState run,
        IEnumerable<WorkflowStepDefinition> steps)
        => [.. steps.SelectMany(step => CollectStepWorkerIds(run, step)).Distinct()];

    private static IReadOnlyList<WorkerId> GetOutstandingWorkerIdsBeforeStep(
        WorkflowRunState run,
        IReadOnlyList<WorkflowStepDefinition> steps,
        string stepName)
    {
        var outstanding = new List<WorkerId>();
        foreach (var step in steps)
        {
            if (string.Equals(step.Name, stepName, StringComparison.Ordinal))
            {
                return [.. outstanding.Distinct()];
            }

            if (step is JoinWorkflowStepDefinition)
            {
                if (run.GetStepStatus(step.Name) == WorkflowStepRunStatus.Completed)
                {
                    outstanding.Clear();
                }

                continue;
            }

            if (run.GetStepStatus(step.Name) == WorkflowStepRunStatus.Completed)
            {
                outstanding.AddRange(CollectStepWorkerIds(run, step));
            }
        }

        return [];
    }

    private static IEnumerable<WorkerId> CollectStepWorkerIds(
        WorkflowRunState run,
        WorkflowStepDefinition step)
    {
        foreach (var workerId in run.GetStepWorkerIds(step.Name))
        {
            yield return workerId;
        }

        var childSteps = step switch
        {
            ParallelWorkflowStepDefinition parallel => parallel.Steps,
            BranchWorkflowStepDefinition branch => branch.Steps,
            _ => [],
        };

        foreach (var workerId in childSteps.SelectMany(child => CollectStepWorkerIds(run, child)))
        {
            yield return workerId;
        }
    }

    private async Task<DispatchResult> Dispatch(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        run.MarkStepRunning(step.Name);
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        var workDefinitionName = step.WorkDefinition.Name;
        var input = WorkflowExecutionSupport.AddWorkflowIdentifiers(
            WorkflowExecutionSupport.ResolveDispatchInput(step, run),
            run.Id,
            run.DefinitionName,
            step.Name);
        var handle = await session.Queue.Enqueue(workDefinitionName, input, cancellationToken: cancellationToken);
        if (!handle.QueueOutcome.IsAccepted)
        {
            run.FailStep(step.Name, handle.QueueOutcome.Messages);
            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return new DispatchResult(false, null, handle.QueueOutcome.Messages);
        }

        run.MarkStepCompleted(step.Name, handle.WorkerId is { } workerId ? [workerId] : []);
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        return new DispatchResult(true, handle, []);
    }

    private async Task<DispatchEachResult> DispatchEach(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        IWorkSystemSession session,
        DispatchEachWorkflowStepDefinition step,
        IReadOnlyDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        CancellationToken cancellationToken)
    {
        run.MarkStepRunning(step.Name);
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        var workDefinitionName = step.WorkDefinition.Name;

        if (!run.TryGetStepWorkerIds(step.SourceStep.StepName, out var sourceWorkerIds) ||
            sourceWorkerIds.Count == 0)
        {
            return new DispatchEachResult(
                false,
                [],
                WorkflowRunStatus.Failed,
                [WorkMessage.Error(
                    "workable.workflow.dispatch_each.source_step_not_ready",
                    $"Workflow step '{step.Name}' could not expand source step '{step.SourceStep.StepName}' because it did not produce any child workers.",
                    "workflow.dispatch_each")]);
        }

        var sources = await WorkflowExecutionSupport.CollectDispatchEachSourceOutputs(
            run,
            workflow,
            sourceWorkerIds,
            (workerId, waitCancellationToken) => this.WaitForWorkerCompletion(
                run,
                workerId,
                activeHandles,
                workerHandleFactory,
                waitCancellationToken),
            cancellationToken);
        if (!sources.IsSuccessful)
        {
            return new DispatchEachResult(false, [], sources.FailureStatus, sources.Messages);
        }

        var expansion = WorkflowExecutionSupport.CreateDispatchEachInputs(step, sources.Outputs);
        if (expansion.Messages.Count > 0)
        {
            return new DispatchEachResult(false, [], WorkflowRunStatus.Failed, expansion.Messages);
        }

        var dispatchedHandles = new List<IWorkerHandle>();
        foreach (var input in expansion.Inputs.Select(itemInput => WorkflowExecutionSupport.AddWorkflowIdentifiers(
                     itemInput,
                     run.Id,
                     run.DefinitionName,
                     step.Name)))
        {
            var handle = await session.Queue.Enqueue(workDefinitionName, input, cancellationToken: cancellationToken);
            if (!handle.QueueOutcome.IsAccepted)
            {
                return new DispatchEachResult(false, [], WorkflowRunStatus.Failed, handle.QueueOutcome.Messages);
            }

            dispatchedHandles.Add(handle);
        }

        run.MarkStepCompleted(step.Name, dispatchedHandles
            .Where(handle => handle.WorkerId is not null)
            .Select(handle => handle.WorkerId!.Value)
            .ToArray());
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        return new DispatchEachResult(true, dispatchedHandles, WorkflowRunStatus.Completed, []);
    }

    private async Task<WorkflowRunCompletion> WaitForJoinOutstanding(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        string joinStepName,
        IReadOnlyDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        IReadOnlyList<WorkerId>? fallbackOutstandingWorkerIds,
        CancellationToken cancellationToken)
    {
        var outstanding = run.GetStepWorkerIds(joinStepName)
            .Distinct()
            .ToList();
        if (outstanding.Count == 0)
        {
            outstanding = (fallbackOutstandingWorkerIds ?? run.GetOutstandingWorkerIds())
                .Distinct()
                .ToList();
            if (outstanding.Count > 0)
            {
                run.MarkStepRunning(joinStepName, outstanding);
            }
        }

        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = outstanding.ToDictionary(
            static workerId => workerId,
            workerId => this.WaitForWorkerCompletion(
                run,
                workerId,
                activeHandles,
                workerHandleFactory,
                pendingCancellation.Token));
        var completions = new WorkflowChildCompletionQueue(
            pending.Select(static item => (item.Key, item.Value)));
        try
        {
            for (var remaining = pending.Count; remaining > 0; remaining--)
            {
                var completed = await completions.ReadAsync(cancellationToken);
                var completion = completed.Completion;
                var status = WorkflowExecutionSupport.ToWorkflowStatus(
                    completion.Status,
                    completion.Status == WorkCompletionStatus.Canceled
                        ? WorkflowExecutionSupport.ResolveCanceledChildBehavior(run, workflow, completed.WorkerId)
                        : WorkflowCanceledChildBehavior.Block);
                if (status != WorkflowRunStatus.Completed)
                {
                    return new WorkflowRunCompletion(
                        status,
                        null,
                        completion.Messages);
                }

                run.RemoveStepWorkerId(joinStepName, completed.WorkerId);
                var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
                publisher.StepUpdated(run.ToSnapshot(), joinStepName);
            }

            return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
        }
        finally
        {
            pendingCancellation.Cancel();
        }
    }

    private async Task<WorkCompletion> WaitForWorkerCompletion(
        WorkflowRunState run,
        WorkerId workerId,
        IReadOnlyDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        CancellationToken cancellationToken)
    {
        if (run.TryGetChildReceipt(workerId, out var receipt) &&
            receipt is not null)
        {
            return WorkflowExecutionSupport.FromReceipt(receipt);
        }

        var handle = activeHandles.TryGetValue(workerId, out var activeHandle)
            ? activeHandle
            : workerHandleFactory(workerId);
        return await handle.WaitForCompletion(cancellationToken);
    }

    private static WorkflowRunCompletion CompleteFromChildOutcome(
        WorkflowRunState run,
        WorkflowRunCompletion completion)
        => completion.Status switch
        {
            WorkflowRunStatus.Blocked => run.Block(completion.Messages),
            WorkflowRunStatus.Canceled => run.Cancel(cancelOutstandingChildren: true),
            _ => run.Fail(completion.Messages),
        };

    private sealed record DispatchResult(
        bool IsAccepted,
        IWorkerHandle? Handle,
        IReadOnlyList<WorkMessage> Messages);

    private sealed record DispatchEachResult(
        bool IsAccepted,
        IReadOnlyList<IWorkerHandle> Handles,
        WorkflowRunStatus FailureStatus,
        IReadOnlyList<WorkMessage> Messages);
}

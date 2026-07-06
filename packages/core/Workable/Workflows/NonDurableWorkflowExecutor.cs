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
        var activeHandles = new Dictionary<WorkerId, IWorkerHandle>();

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
                            break;
                        }
                    case DispatchEachWorkflowStepDefinition dispatchEach:
                        {
                            var result = await this.DispatchEach(
                                run,
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

                                run.FailStep(dispatchEach.Name, result.Messages);
                                publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                                return run.Fail(result.Messages);
                            }

                            foreach (var handle in result.Handles.Where(handle => handle.WorkerId is not null))
                            {
                                activeHandles[handle.WorkerId!.Value] = handle;
                            }

                            break;
                        }
                    case ParallelWorkflowStepDefinition parallel:
                        {
                            run.MarkStepRunning(parallel.Name);
                            publisher.StepUpdated(run.ToSnapshot(), parallel.Name);
                            var workerIds = new List<WorkerId>();
                            foreach (var child in parallel.Steps.OfType<DispatchWorkflowStepDefinition>())
                            {
                                var input = WorkflowExecutionSupport.AddWorkflowIdentifiers(
                                    WorkflowExecutionSupport.ResolveDispatchInput(child, run),
                                    run.Id,
                                    run.DefinitionName,
                                    child.Name);
                                var handle = await session.Queue.Enqueue(
                                    child.WorkDefinition.Name,
                                    input,
                                    cancellationToken: cancellationToken);
                                if (!handle.QueueOutcome.IsAccepted)
                                {
                                    run.FailStep(parallel.Name, handle.QueueOutcome.Messages);
                                    publisher.StepUpdated(run.ToSnapshot(), parallel.Name);
                                    return run.Fail(handle.QueueOutcome.Messages);
                                }

                                if (handle.WorkerId is { } childWorkerId)
                                {
                                    workerIds.Add(childWorkerId);
                                    activeHandles[childWorkerId] = handle;
                                }
                            }

                            run.MarkStepCompleted(parallel.Name, workerIds);
                            publisher.StepUpdated(run.ToSnapshot(), parallel.Name);
                            break;
                        }
                    case JoinWorkflowStepDefinition join:
                        {
                            if (status == WorkflowStepRunStatus.Pending)
                            {
                                run.MarkStepRunning(join.Name, run.GetOutstandingWorkerIds());
                                publisher.StepUpdated(run.ToSnapshot(), join.Name);
                            }

                            var completion = await this.WaitForJoinOutstanding(run, join.Name, activeHandles, workerHandleFactory, cancellationToken);
                            if (!completion.IsCompletedSuccessfully)
                            {
                                if (completion.Status != WorkflowRunStatus.Blocked)
                                {
                                    run.FailStep(join.Name, completion.Messages);
                                }

                                publisher.StepUpdated(run.ToSnapshot(), join.Name);
                                return completion.Status == WorkflowRunStatus.Blocked
                                    ? run.Block(completion.Messages)
                                    : run.Fail(completion.Messages);
                            }

                            run.MarkStepCompleted(join.Name);
                            publisher.StepUpdated(run.ToSnapshot(), join.Name);
                            break;
                        }
                    default:
                        return run.Fail(
                            [WorkMessage.Error(
                                "workable.workflow.step.unsupported",
                                $"Workflow step '{step.Name}' uses unsupported kind '{step.Kind}'.",
                                "workflow.step")]);
                }
            }

            if (run.GetOutstandingWorkerIds().Count > 0)
            {
                var completion = await WorkflowExecutionSupport.WaitForOutstanding(
                    run.GetOutstandingWorkerIds(),
                    workerId => activeHandles.TryGetValue(workerId, out var handle)
                        ? handle
                        : workerHandleFactory(workerId),
                    cancellationToken);
                if (!completion.IsCompletedSuccessfully)
                {
                    return completion.Status == WorkflowRunStatus.Blocked
                        ? run.Block(completion.Messages)
                        : run.Fail(completion.Messages);
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

        var outputs = new List<WorkOutput?>();
        foreach (var workerId in sourceWorkerIds.Distinct())
        {
            var completion = await this.WaitForWorkerCompletion(run, workerId, activeHandles, workerHandleFactory, cancellationToken);
            if (completion.Status != WorkCompletionStatus.Completed)
            {
                return new DispatchEachResult(
                    false,
                    [],
                    WorkflowExecutionSupport.ToWorkflowStatus(completion.Status),
                    completion.Messages);
            }

            outputs.Add(completion.Output);
        }

        var expansion = WorkflowExecutionSupport.CreateDispatchEachInputs(step, outputs);
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
        string joinStepName,
        IReadOnlyDictionary<WorkerId, IWorkerHandle> activeHandles,
        Func<WorkerId, IWorkerHandle> workerHandleFactory,
        CancellationToken cancellationToken)
    {
        var outstanding = run.GetStepWorkerIds(joinStepName)
            .Distinct()
            .ToList();
        if (outstanding.Count == 0)
        {
            outstanding = run.GetOutstandingWorkerIds()
                .Distinct()
                .ToList();
            if (outstanding.Count > 0)
            {
                run.MarkStepRunning(joinStepName, outstanding);
            }
        }

        while (outstanding.Count > 0)
        {
            var workerId = outstanding[0];
            if (run.TryGetChildReceipt(workerId, out var receipt) &&
                receipt is not null)
            {
                if (receipt.CompletionStatus != WorkCompletionStatus.Completed)
                {
                    return new WorkflowRunCompletion(
                        WorkflowExecutionSupport.ToWorkflowStatus(receipt.CompletionStatus),
                        null,
                        receipt.Messages);
                }

                outstanding.RemoveAt(0);
                run.RemoveStepWorkerId(joinStepName, workerId);
                var receiptPublisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
                receiptPublisher.StepUpdated(run.ToSnapshot(), joinStepName);
                continue;
            }

            var handle = activeHandles.TryGetValue(workerId, out var activeHandle)
                ? activeHandle
                : workerHandleFactory(workerId);
            var completion = await handle.WaitForCompletion(cancellationToken);
            if (completion.Status != WorkCompletionStatus.Completed)
            {
                return new WorkflowRunCompletion(
                    WorkflowExecutionSupport.ToWorkflowStatus(completion.Status),
                    null,
                    completion.Messages);
            }

            outstanding.RemoveAt(0);
            run.RemoveStepWorkerId(joinStepName, workerId);
            var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
            publisher.StepUpdated(run.ToSnapshot(), joinStepName);
        }

        return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
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

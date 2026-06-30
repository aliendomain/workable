namespace Workable;

internal sealed class DurableWorkflowExecutor(
    string workSystemKey,
    Func<string, RegisteredWork?> getRegisteredWork,
    Func<WorkRequestContext, IWorkSystemSession> createSession,
    Func<WorkerId, IWorkerHandle> createWorkerHandle,
    WorkflowPersistenceCoordinator persistence,
    WorkflowEventPublisher? workflowEvents = null,
    Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker = null)
{
    private static readonly TimeSpan WorkerObservationPollInterval = TimeSpan.FromMilliseconds(100);

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
        IWorkSystemSession? session = null;
        isPauseRequested ??= static () => false;
        isCancelRequested ??= static () => false;
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        try
        {
            run.MarkRunning();
            session = createSession(run.RequestContext);
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
                            var messages = await this.Dispatch(run, session, dispatch, cancellationToken);
                            if (messages.Count > 0)
                            {
                                run.FailStep(dispatch.Name, messages);
                                publisher.StepUpdated(run.ToSnapshot(), dispatch.Name);
                                return await this.DeleteFailedRun(run, messages, cancellationToken);
                            }

                            break;
                        }
                    case DispatchEachWorkflowStepDefinition dispatchEach:
                        {
                            var outcome = await this.DispatchEach(run, session, dispatchEach, cancellationToken);
                            if (!outcome.IsAccepted)
                            {
                                if (outcome.FailureStatus == WorkflowRunStatus.Blocked)
                                {
                                    publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                                    return await this.UpsertBlockedRun(run, outcome.Messages, cancellationToken);
                                }

                                run.FailStep(dispatchEach.Name, outcome.Messages);
                                publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                                return await this.DeleteFailedRun(run, outcome.Messages, cancellationToken);
                            }

                            break;
                        }
                    case ParallelWorkflowStepDefinition parallel:
                        {
                            var messages = await this.DispatchParallel(run, session, parallel, cancellationToken);
                            if (messages.Count > 0)
                            {
                                run.FailStep(parallel.Name, messages);
                                publisher.StepUpdated(run.ToSnapshot(), parallel.Name);
                                return await this.DeleteFailedRun(run, messages, cancellationToken);
                            }

                            break;
                        }
                    case JoinWorkflowStepDefinition join:
                        {
                            if (status == WorkflowStepRunStatus.Pending)
                            {
                                run.MarkStepRunning(join.Name, run.GetOutstandingWorkerIds());
                                await persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
                                publisher.StepUpdated(run.ToSnapshot(), join.Name);
                            }

                            var completion = await this.WaitForJoinOutstanding(run, session, join.Name, cancellationToken);
                            if (!completion.IsCompletedSuccessfully)
                            {
                                if (completion.Status != WorkflowRunStatus.Blocked)
                                {
                                    run.FailStep(join.Name, completion.Messages);
                                }

                                publisher.StepUpdated(run.ToSnapshot(), join.Name);
                                return completion.Status == WorkflowRunStatus.Blocked
                                    ? await this.UpsertBlockedRun(run, completion.Messages, cancellationToken)
                                    : await this.DeleteFailedRun(run, completion.Messages, cancellationToken);
                            }

                            run.MarkStepCompleted(join.Name);
                            await persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
                            publisher.StepUpdated(run.ToSnapshot(), join.Name);
                            break;
                        }
                    default:
                        return await this.DeleteFailedRun(
                            run,
                            [WorkMessage.Error(
                                "workable.workflow.step.unsupported",
                                $"Workflow step '{step.Name}' uses unsupported kind '{step.Kind}'.",
                                "workflow.step")],
                            cancellationToken);
                }
            }

            var trailingCompletion = await this.WaitForOutstandingWorkers(
                run,
                session,
                run.GetOutstandingWorkerIds(),
                cancellationToken);
            if (!trailingCompletion.IsCompletedSuccessfully)
            {
                return trailingCompletion.Status == WorkflowRunStatus.Blocked
                    ? await this.UpsertBlockedRun(run, trailingCompletion.Messages, cancellationToken)
                    : await this.DeleteFailedRun(run, trailingCompletion.Messages, cancellationToken);
            }

            var success = run.Complete();
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isCancelRequested() || !isPauseRequested())
            {
                return run.Cancel();
            }

            if (session is not null)
            {
                await WorkflowExecutionSupport.PauseOutstandingChildren(run, session, getAuthoritativeWorker, CancellationToken.None);
            }

            var paused = run.Pause();
            await persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            return paused;
        }
        catch (Exception exception)
        {
            return await this.DeleteFailedRun(
                run,
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")],
                CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<WorkMessage>> Dispatch(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        var workDefinitionName = step.WorkDefinition.Name;
        var registeredWork = getRegisteredWork(workDefinitionName)
            ?? throw new InvalidOperationException($"Workflow step '{step.Name}' targets unknown work '{workDefinitionName}'.");
        run.MarkStepRunning(step.Name);
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        publisher.StepUpdated(run.ToSnapshot(), step.Name);

        try
        {
            await persistence.ExecuteTransaction(
                async (transaction, transactionOptions, transactionCancellationToken) =>
                {
                    var handle = await session.Queue.Enqueue(
                            workDefinitionName,
                            WorkflowExecutionSupport.AddWorkflowIdentifiers(
                                step.Input,
                                run.Id,
                                run.DefinitionName,
                                step.Name),
                            CreateDurableChildOptions(registeredWork, transactionOptions),
                            transactionCancellationToken);
                    if (!handle.QueueOutcome.IsAccepted || handle.WorkerId is not { } workerId)
                    {
                        throw new WorkflowDispatchRejectedException(handle.QueueOutcome.Messages);
                    }

                    run.MarkStepCompleted(step.Name, [workerId]);
                    await persistence.UpsertRun(
                        this.CreatePersistenceRecord(run),
                        transaction,
                        transactionCancellationToken);
                },
                cancellationToken);

            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return [];
        }
        catch (WorkflowDispatchRejectedException rejection)
        {
            return rejection.Messages;
        }
    }

    private async Task<DispatchEachOutcome> DispatchEach(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchEachWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        var workDefinitionName = step.WorkDefinition.Name;
        var registeredWork = getRegisteredWork(workDefinitionName)
            ?? throw new InvalidOperationException($"Workflow step '{step.Name}' targets unknown work '{workDefinitionName}'.");
        run.MarkStepRunning(step.Name);
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        publisher.StepUpdated(run.ToSnapshot(), step.Name);

        if (!run.TryGetStepWorkerIds(step.SourceStep.StepName, out var sourceWorkerIds) ||
            sourceWorkerIds.Count == 0)
        {
            return new DispatchEachOutcome(
                false,
                WorkflowRunStatus.Failed,
                [WorkMessage.Error(
                    "workable.workflow.dispatch_each.source_step_not_ready",
                    $"Workflow step '{step.Name}' could not expand source step '{step.SourceStep.StepName}' because it did not produce any child workers.",
                    "workflow.dispatch_each")]);
        }

        var outputs = new List<WorkOutput?>();
        foreach (var workerId in sourceWorkerIds.Distinct())
        {
            var completion = await this.WaitForWorkerCompletion(run, session, workerId, cancellationToken);
            if (completion.Status != WorkCompletionStatus.Completed)
            {
                return new DispatchEachOutcome(
                    false,
                    WorkflowExecutionSupport.ToWorkflowStatus(completion.Status),
                    completion.Messages);
            }

            outputs.Add(completion.Output);
        }

        var expansion = WorkflowExecutionSupport.CreateDispatchEachInputs(step, outputs);
        if (expansion.Messages.Count > 0)
        {
            return new DispatchEachOutcome(false, WorkflowRunStatus.Failed, expansion.Messages);
        }

        try
        {
            await persistence.ExecuteTransaction(
                async (transaction, transactionOptions, transactionCancellationToken) =>
                {
                    var workerIds = new List<WorkerId>();
                    foreach (var itemInput in expansion.Inputs)
                    {
                        var handle = await session.Queue.Enqueue(
                            workDefinitionName,
                            WorkflowExecutionSupport.AddWorkflowIdentifiers(
                                itemInput,
                                run.Id,
                                run.DefinitionName,
                                step.Name),
                            CreateDurableChildOptions(registeredWork, transactionOptions),
                            transactionCancellationToken);
                        if (!handle.QueueOutcome.IsAccepted || handle.WorkerId is not { } childWorkerId)
                        {
                            throw new WorkflowDispatchRejectedException(handle.QueueOutcome.Messages);
                        }

                        workerIds.Add(childWorkerId);
                    }

                    run.MarkStepCompleted(step.Name, workerIds);
                    await persistence.UpsertRun(
                        this.CreatePersistenceRecord(run),
                        transaction,
                        transactionCancellationToken);
                },
                cancellationToken);

            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return new DispatchEachOutcome(true, WorkflowRunStatus.Completed, []);
        }
        catch (WorkflowDispatchRejectedException rejection)
        {
            return new DispatchEachOutcome(false, WorkflowRunStatus.Failed, rejection.Messages);
        }
    }

    private async Task<IReadOnlyList<WorkMessage>> DispatchParallel(
        WorkflowRunState run,
        IWorkSystemSession session,
        ParallelWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        run.MarkStepRunning(step.Name);
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        publisher.StepUpdated(run.ToSnapshot(), step.Name);

        try
        {
            await persistence.ExecuteTransaction(
                async (transaction, transactionOptions, transactionCancellationToken) =>
                {
                    var workerIds = new List<WorkerId>();
                    foreach (var child in step.Steps.OfType<DispatchWorkflowStepDefinition>())
                    {
                        var childWorkDefinitionName = child.WorkDefinition.Name;
                        var registeredWork = getRegisteredWork(childWorkDefinitionName)
                            ?? throw new InvalidOperationException($"Workflow step '{child.Name}' targets unknown work '{childWorkDefinitionName}'.");
                        var handle = await session.Queue.Enqueue(
                            childWorkDefinitionName,
                            WorkflowExecutionSupport.AddWorkflowIdentifiers(
                                child.Input,
                                run.Id,
                                run.DefinitionName,
                                child.Name),
                            CreateDurableChildOptions(registeredWork, transactionOptions),
                            transactionCancellationToken);
                        if (!handle.QueueOutcome.IsAccepted || handle.WorkerId is not { } childWorkerId)
                        {
                            throw new WorkflowDispatchRejectedException(handle.QueueOutcome.Messages);
                        }

                        workerIds.Add(childWorkerId);
                    }

                    run.MarkStepCompleted(step.Name, workerIds);
                    await persistence.UpsertRun(
                        this.CreatePersistenceRecord(run),
                        transaction,
                        transactionCancellationToken);
                },
                cancellationToken);

            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return [];
        }
        catch (WorkflowDispatchRejectedException rejection)
        {
            return rejection.Messages;
        }
    }

    private WorkflowRunPersistenceRecord CreatePersistenceRecord(WorkflowRunState run)
        => run.ToPersistenceRecord(workSystemKey);

    private async Task<WorkflowRunCompletion> WaitForJoinOutstanding(
        WorkflowRunState run,
        IWorkSystemSession session,
        string joinStepName,
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
                await persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            }
        }

        while (outstanding.Count > 0)
        {
            var workerId = outstanding[0];
            var completion = await this.WaitForWorkerCompletion(run, session, workerId, cancellationToken);
            if (!completion.IsCompletedSuccessfully)
            {
                return new WorkflowRunCompletion(
                    WorkflowExecutionSupport.ToWorkflowStatus(completion.Status),
                    null,
                    completion.Messages);
            }

            outstanding.RemoveAt(0);
            run.RemoveStepWorkerId(joinStepName, workerId);
            await persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
            publisher.StepUpdated(run.ToSnapshot(), joinStepName);
        }

        return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
    }

    private async Task<WorkflowRunCompletion> WaitForOutstandingWorkers(
        WorkflowRunState run,
        IWorkSystemSession session,
        IReadOnlyList<WorkerId> workerIds,
        CancellationToken cancellationToken)
    {
        foreach (var workerId in workerIds.Distinct())
        {
            var completion = await this.WaitForWorkerCompletion(run, session, workerId, cancellationToken);
            if (!completion.IsCompletedSuccessfully)
            {
                return new WorkflowRunCompletion(
                    WorkflowExecutionSupport.ToWorkflowStatus(completion.Status),
                    null,
                    completion.Messages);
            }
        }

        return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
    }

    private async Task<WorkCompletion> WaitForWorkerCompletion(
        WorkflowRunState run,
        IWorkSystemSession session,
        WorkerId workerId,
        CancellationToken cancellationToken)
    {
        if (run.TryGetChildReceipt(workerId, out var receipt) &&
            receipt is not null)
        {
            return WorkflowExecutionSupport.FromReceipt(receipt);
        }

        var snapshot = getAuthoritativeWorker is not null
            ? await getAuthoritativeWorker(workerId, cancellationToken)
            : await session.Query.Worker(workerId, cancellationToken);
        if (snapshot is not null)
        {
            var status = WorkerStateMachine.CompletionStatusFor(snapshot.State);
            if (status != WorkCompletionStatus.Invalid)
            {
                return new WorkCompletion(
                    status,
                    snapshot,
                    snapshot.Output,
                    snapshot.Messages);
            }
        }

        if (!await persistence.DurableWorkerExists(workerId, cancellationToken))
        {
            return new WorkCompletion(
                WorkCompletionStatus.NotFound,
                null,
                null,
                [WorkMessage.Error(
                    "workable.workflow.child.not_found",
                    $"Workflow child worker '{workerId.Value:D}' could not be recovered because its durable state no longer exists.",
                    "workflow.execution")]);
        }

        var handle = createWorkerHandle(workerId);
        var handleCompletion = handle.WaitForCompletion(cancellationToken);
        while (true)
        {
            snapshot = getAuthoritativeWorker is not null
                ? await getAuthoritativeWorker(workerId, cancellationToken)
                : await session.Query.Worker(workerId, cancellationToken);
            if (snapshot is not null)
            {
                var status = WorkerStateMachine.CompletionStatusFor(snapshot.State);
                if (status != WorkCompletionStatus.Invalid)
                {
                    return new WorkCompletion(
                        status,
                        snapshot,
                        snapshot.Output,
                        snapshot.Messages);
                }
            }

            if (!await persistence.DurableWorkerExists(workerId, cancellationToken))
            {
                return new WorkCompletion(
                    WorkCompletionStatus.NotFound,
                    null,
                    null,
                    [WorkMessage.Error(
                        "workable.workflow.child.not_found",
                        $"Workflow child worker '{workerId.Value:D}' could not be recovered because its durable state no longer exists.",
                        "workflow.execution")]);
            }

            var completed = await Task.WhenAny(
                handleCompletion,
                Task.Delay(WorkerObservationPollInterval, cancellationToken));
            if (completed == handleCompletion)
            {
                return await handleCompletion;
            }
        }
    }

    private async Task<WorkflowRunCompletion> DeleteFailedRun(
        WorkflowRunState run,
        IReadOnlyList<WorkMessage> messages,
        CancellationToken cancellationToken)
    {
        var failure = run.Fail(messages);
        await persistence.UpsertRun(this.CreatePersistenceRecord(run), cancellationToken);
        return failure;
    }

    private async Task<WorkflowRunCompletion> UpsertBlockedRun(
        WorkflowRunState run,
        IReadOnlyList<WorkMessage> messages,
        CancellationToken cancellationToken)
    {
        var blocked = run.Block(messages);
        await persistence.UpsertRun(this.CreatePersistenceRecord(run), cancellationToken);
        return blocked;
    }

    private sealed record DispatchEachOutcome(
        bool IsAccepted,
        WorkflowRunStatus FailureStatus,
        IReadOnlyList<WorkMessage> Messages);

    private static WorkerOptions CreateDurableChildOptions(
        RegisteredWork registeredWork,
        WorkerOptions transactionOptions)
    {
        var configuration = registeredWork.DefaultRuntimePlan.Configuration;
        return new WorkerOptions(
            configuration with
            {
                Coordination = configuration.Coordination with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = configuration.Coordination.Durability with
                    {
                        IsEnabled = true,
                    },
                },
            },
            transactionOptions.QueueDurabilityTransaction);
    }

    private sealed class WorkflowDispatchRejectedException(IReadOnlyList<WorkMessage> messages)
        : Exception("Workflow child dispatch was rejected.")
    {
        public IReadOnlyList<WorkMessage> Messages { get; } = messages;
    }
}

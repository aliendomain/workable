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
        var persistenceGate = new WorkflowRunPersistenceGate();
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

                var stepCompletion = await this.ExecuteStep(
                    run,
                    session,
                    step,
                    persistenceGate,
                    scopedJoinWorkerIds: null,
                    cancellationToken);
                if (stepCompletion is not null)
                {
                    return stepCompletion;
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
                    ? await this.UpsertBlockedRun(run, trailingCompletion.Messages, persistenceGate, cancellationToken)
                    : await this.DeleteFailedRun(run, trailingCompletion.Messages, persistenceGate, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
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
            await this.UpsertRun(run, persistenceGate, CancellationToken.None);
            return paused;
        }
        catch (Exception exception) when (ShouldHandleExecutionException(exception))
        {
            return await this.DeleteFailedRun(
                run,
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")],
                persistenceGate,
                cancellationToken);
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
        IWorkSystemSession session,
        WorkflowStepDefinition step,
        WorkflowRunPersistenceGate persistenceGate,
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
                    var messages = await this.Dispatch(run, session, dispatch, persistenceGate, cancellationToken);
                    if (messages.Count > 0)
                    {
                        run.FailStep(dispatch.Name, messages);
                        publisher.StepUpdated(run.ToSnapshot(), dispatch.Name);
                        return await this.DeleteFailedRun(run, messages, persistenceGate, cancellationToken);
                    }

                    return null;
                }
            case DispatchEachWorkflowStepDefinition dispatchEach:
                {
                    var outcome = await this.DispatchEach(run, session, dispatchEach, persistenceGate, cancellationToken);
                    if (!outcome.IsAccepted)
                    {
                        if (outcome.FailureStatus == WorkflowRunStatus.Blocked)
                        {
                            publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                            return await this.UpsertBlockedRun(run, outcome.Messages, persistenceGate, cancellationToken);
                        }

                        run.FailStep(dispatchEach.Name, outcome.Messages);
                        publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                        return await this.DeleteFailedRun(run, outcome.Messages, persistenceGate, cancellationToken);
                    }

                    return null;
                }
            case ParallelWorkflowStepDefinition parallel:
                return await this.ExecuteParallel(run, session, parallel, persistenceGate, cancellationToken);
            case BranchWorkflowStepDefinition branch:
                return await this.ExecuteBranch(run, session, branch, persistenceGate, cancellationToken);
            case JoinWorkflowStepDefinition join:
                {
                    if (status == WorkflowStepRunStatus.Pending)
                    {
                        run.MarkStepRunning(join.Name, scopedJoinWorkerIds ?? run.GetOutstandingWorkerIds());
                        await this.UpsertRun(run, persistenceGate, CancellationToken.None);
                        publisher.StepUpdated(run.ToSnapshot(), join.Name);
                    }

                    var completion = await this.WaitForJoinOutstanding(
                        run,
                        session,
                        join.Name,
                        persistenceGate,
                        scopedJoinWorkerIds,
                        cancellationToken);
                    if (!completion.IsCompletedSuccessfully)
                    {
                        if (completion.Status != WorkflowRunStatus.Blocked)
                        {
                            run.FailStep(join.Name, completion.Messages);
                        }

                        publisher.StepUpdated(run.ToSnapshot(), join.Name);
                        return completion.Status == WorkflowRunStatus.Blocked
                            ? await this.UpsertBlockedRun(run, completion.Messages, persistenceGate, cancellationToken)
                            : await this.DeleteFailedRun(run, completion.Messages, persistenceGate, cancellationToken);
                    }

                    run.MarkStepCompleted(join.Name);
                    await this.UpsertRun(run, persistenceGate, CancellationToken.None);
                    publisher.StepUpdated(run.ToSnapshot(), join.Name);
                    return null;
                }
            default:
                return await this.DeleteFailedRun(
                    run,
                    [WorkMessage.Error(
                        "workable.workflow.step.unsupported",
                        $"Workflow step '{step.Name}' uses unsupported kind '{step.Kind}'.",
                        "workflow.step")],
                    persistenceGate,
                    cancellationToken);
        }
    }

    private async Task<WorkflowRunCompletion?> ExecuteParallel(
        WorkflowRunState run,
        IWorkSystemSession session,
        ParallelWorkflowStepDefinition step,
        WorkflowRunPersistenceGate persistenceGate,
        CancellationToken cancellationToken)
    {
        var publisher = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        run.MarkStepRunning(step.Name);
        publisher.StepUpdated(run.ToSnapshot(), step.Name);

        var childTasks = step.Steps
            .Select(child => this.ExecuteStep(
                run,
                session,
                child,
                persistenceGate,
                scopedJoinWorkerIds: null,
                cancellationToken))
            .ToArray();
        var childCompletions = await Task.WhenAll(childTasks);
        foreach (var completion in childCompletions.Where(static completion => completion is not null))
        {
            run.FailStep(step.Name, completion!.Messages);
            await this.UpsertRun(run, persistenceGate, CancellationToken.None);
            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return new WorkflowRunCompletion(completion.Status, run.ToSnapshot(), completion.Messages);
        }

        run.MarkStepCompleted(step.Name, CollectStepWorkerIds(run, step.Steps));
        await this.UpsertRun(run, persistenceGate, CancellationToken.None);
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        return null;
    }

    private async Task<WorkflowRunCompletion?> ExecuteBranch(
        WorkflowRunState run,
        IWorkSystemSession session,
        BranchWorkflowStepDefinition step,
        WorkflowRunPersistenceGate persistenceGate,
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
                session,
                child,
                persistenceGate,
                scopedJoinWorkerIds,
                cancellationToken);
            if (completion is not null)
            {
                run.FailStep(step.Name, completion.Messages);
                await this.UpsertRun(run, persistenceGate, CancellationToken.None);
                publisher.StepUpdated(run.ToSnapshot(), step.Name);
                return new WorkflowRunCompletion(completion.Status, run.ToSnapshot(), completion.Messages);
            }
        }

        run.MarkStepCompleted(step.Name, CollectStepWorkerIds(run, step.Steps));
        await this.UpsertRun(run, persistenceGate, CancellationToken.None);
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

    private async Task<IReadOnlyList<WorkMessage>> Dispatch(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchWorkflowStepDefinition step,
        WorkflowRunPersistenceGate persistenceGate,
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
            await persistenceGate.Run(
                () => persistence.ExecuteTransaction(
                    async (transaction, transactionOptions, transactionCancellationToken) =>
                    {
                        var handle = await session.Queue.Enqueue(
                                workDefinitionName,
                                WorkflowExecutionSupport.AddWorkflowIdentifiers(
                                    WorkflowExecutionSupport.ResolveDispatchInput(step, run),
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
                    cancellationToken),
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
        WorkflowRunPersistenceGate persistenceGate,
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
            await persistenceGate.Run(
                () => persistence.ExecuteTransaction(
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
                    cancellationToken),
                cancellationToken);

            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return new DispatchEachOutcome(true, WorkflowRunStatus.Completed, []);
        }
        catch (WorkflowDispatchRejectedException rejection)
        {
            return new DispatchEachOutcome(false, WorkflowRunStatus.Failed, rejection.Messages);
        }
    }

    private WorkflowRunPersistenceRecord CreatePersistenceRecord(WorkflowRunState run)
        => run.ToPersistenceRecord(workSystemKey);

    private async Task UpsertRun(
        WorkflowRunState run,
        WorkflowRunPersistenceGate persistenceGate,
        CancellationToken cancellationToken)
        => await persistenceGate.Run(
            () => persistence.UpsertRun(this.CreatePersistenceRecord(run), cancellationToken),
            cancellationToken);

    private async Task<WorkflowRunCompletion> WaitForJoinOutstanding(
        WorkflowRunState run,
        IWorkSystemSession session,
        string joinStepName,
        WorkflowRunPersistenceGate persistenceGate,
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
                await this.UpsertRun(run, persistenceGate, CancellationToken.None);
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
            await this.UpsertRun(run, persistenceGate, CancellationToken.None);
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
            if (handleCompletion.IsCompleted)
            {
                return await handleCompletion;
            }

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

            var completed = await Task.WhenAny(
                handleCompletion,
                Task.Delay(WorkerObservationPollInterval, cancellationToken));
            if (completed == handleCompletion)
            {
                return await handleCompletion;
            }

            if (!await persistence.DurableWorkerExists(workerId, cancellationToken))
            {
                if (handleCompletion.IsCompleted)
                {
                    return await handleCompletion;
                }

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

                return new WorkCompletion(
                    WorkCompletionStatus.NotFound,
                    null,
                    null,
                    [WorkMessage.Error(
                        "workable.workflow.child.not_found",
                        $"Workflow child worker '{workerId.Value:D}' could not be recovered because its durable state no longer exists.",
                        "workflow.execution")]);
            }
        }
    }

    private async Task<WorkflowRunCompletion> DeleteFailedRun(
        WorkflowRunState run,
        IReadOnlyList<WorkMessage> messages,
        WorkflowRunPersistenceGate persistenceGate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failure = run.Fail(messages);
        await this.UpsertRun(run, persistenceGate, cancellationToken);
        return failure;
    }

    private async Task<WorkflowRunCompletion> UpsertBlockedRun(
        WorkflowRunState run,
        IReadOnlyList<WorkMessage> messages,
        WorkflowRunPersistenceGate persistenceGate,
        CancellationToken cancellationToken)
    {
        var blocked = run.Block(messages);
        await this.UpsertRun(run, persistenceGate, cancellationToken);
        return blocked;
    }

    private sealed class WorkflowRunPersistenceGate
    {
        private readonly SemaphoreSlim sync = new(1, 1);

        public async Task Run(
            Func<Task> action,
            CancellationToken cancellationToken)
        {
            await this.sync.WaitAsync(cancellationToken);
            try
            {
                await action();
            }
            finally
            {
                this.sync.Release();
            }
        }
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

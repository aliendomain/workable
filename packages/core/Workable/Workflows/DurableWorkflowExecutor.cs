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
                    workflow,
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
                workflow,
                session,
                run.GetOutstandingWorkerIds(),
                cancellationToken);
            if (!trailingCompletion.IsCompletedSuccessfully)
            {
                return await this.CompleteFromChildOutcome(
                    run,
                    trailingCompletion,
                    persistenceGate,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return run.CreateFinalCompletion(WorkflowRunStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isCancelRequested() || !isPauseRequested())
            {
                return new WorkflowRunCompletion(WorkflowRunStatus.Canceled, null, []);
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
            return this.CreateFailedRunCompletion(
                run,
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")],
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
        RegisteredWorkflow workflow,
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
                        return this.CreateFailedRunCompletion(run, messages, cancellationToken);
                    }

                    return null;
                }
            case DispatchEachWorkflowStepDefinition dispatchEach:
                {
                    var outcome = await this.DispatchEach(
                        run,
                        workflow,
                        session,
                        dispatchEach,
                        persistenceGate,
                        cancellationToken);
                    if (!outcome.IsAccepted)
                    {
                        if (outcome.FailureStatus == WorkflowRunStatus.Blocked)
                        {
                            publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                            return await this.UpsertBlockedRun(run, outcome.Messages, persistenceGate, cancellationToken);
                        }

                        if (outcome.FailureStatus == WorkflowRunStatus.Canceled)
                        {
                            publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                            return await this.CompleteFromChildOutcome(
                                run,
                                new WorkflowRunCompletion(outcome.FailureStatus, null, outcome.Messages),
                                persistenceGate,
                                cancellationToken);
                        }

                        run.FailStep(dispatchEach.Name, outcome.Messages);
                        publisher.StepUpdated(run.ToSnapshot(), dispatchEach.Name);
                        return this.CreateFailedRunCompletion(run, outcome.Messages, cancellationToken);
                    }

                    return null;
                }
            case ParallelWorkflowStepDefinition parallel:
                return await this.ExecuteParallel(run, workflow, session, parallel, persistenceGate, cancellationToken);
            case BranchWorkflowStepDefinition branch:
                return await this.ExecuteBranch(run, workflow, session, branch, persistenceGate, cancellationToken);
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
                        workflow,
                        session,
                        join.Name,
                        persistenceGate,
                        scopedJoinWorkerIds,
                        cancellationToken);
                    if (!completion.IsCompletedSuccessfully)
                    {
                        if (completion.Status == WorkflowRunStatus.Failed)
                        {
                            run.FailStep(join.Name, completion.Messages);
                        }

                        publisher.StepUpdated(run.ToSnapshot(), join.Name);
                        return await this.CompleteFromChildOutcome(
                            run,
                            completion,
                            persistenceGate,
                            cancellationToken);
                    }

                    run.MarkStepCompleted(join.Name);
                    await this.UpsertRun(run, persistenceGate, CancellationToken.None);
                    publisher.StepUpdated(run.ToSnapshot(), join.Name);
                    return null;
                }
            default:
                return this.CreateFailedRunCompletion(
                    run,
                    [WorkMessage.Error(
                        "workable.workflow.step.unsupported",
                        $"Workflow step '{step.Name}' uses unsupported kind '{step.Kind}'.",
                        "workflow.step")],
                    cancellationToken);
        }
    }

    private async Task<WorkflowRunCompletion?> ExecuteParallel(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
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
                workflow,
                session,
                child,
                persistenceGate,
                scopedJoinWorkerIds: null,
                cancellationToken))
            .ToArray();
        var childCompletions = await Task.WhenAll(childTasks);
        foreach (var completion in childCompletions.Where(static completion => completion is not null))
        {
            if (completion!.Status != WorkflowRunStatus.Canceled)
            {
                run.FailStep(step.Name, completion.Messages);
                await this.UpsertRun(run, persistenceGate, CancellationToken.None);
            }
            publisher.StepUpdated(run.ToSnapshot(), step.Name);
            return new WorkflowRunCompletion(
                completion.Status,
                run.ToSnapshot(),
                completion.Messages,
                completion.CancelOutstandingChildren);
        }

        run.MarkStepCompleted(step.Name, CollectStepWorkerIds(run, step.Steps));
        await this.UpsertRun(run, persistenceGate, CancellationToken.None);
        publisher.StepUpdated(run.ToSnapshot(), step.Name);
        return null;
    }

    private async Task<WorkflowRunCompletion?> ExecuteBranch(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
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
                workflow,
                session,
                child,
                persistenceGate,
                scopedJoinWorkerIds,
                cancellationToken);
            if (completion is not null)
            {
                if (completion.Status != WorkflowRunStatus.Canceled)
                {
                    run.FailStep(step.Name, completion.Messages);
                    await this.UpsertRun(run, persistenceGate, CancellationToken.None);
                }
                publisher.StepUpdated(run.ToSnapshot(), step.Name);
                return new WorkflowRunCompletion(
                    completion.Status,
                    run.ToSnapshot(),
                    completion.Messages,
                    completion.CancelOutstandingChildren);
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
                    run.Id,
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
                            run.Id,
                            () => this.CreatePersistenceRecord(run),
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
        RegisteredWorkflow workflow,
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
            (sourceWorkerIds.Count == 0 &&
                run.GetStepStatus(step.SourceStep.StepName) != WorkflowStepRunStatus.Completed))
        {
            return new DispatchEachOutcome(
                false,
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
            (workerId, waitCancellationToken) =>
                this.WaitForWorkerCompletion(run, session, workerId, waitCancellationToken),
            cancellationToken);
        if (!sources.IsSuccessful)
        {
            return new DispatchEachOutcome(false, sources.FailureStatus, sources.Messages);
        }

        var expansion = WorkflowExecutionSupport.CreateDispatchEachInputs(step, sources.Outputs);
        if (expansion.Messages.Count > 0)
        {
            return new DispatchEachOutcome(false, WorkflowRunStatus.Failed, expansion.Messages);
        }

        try
        {
            await persistenceGate.Run(
                () => persistence.ExecuteTransaction(
                    run.Id,
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
                            run.Id,
                            () => this.CreatePersistenceRecord(run),
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
            () => persistence.UpsertRun(run.Id, () => this.CreatePersistenceRecord(run), cancellationToken),
            cancellationToken);

    private async Task<WorkflowRunCompletion> WaitForJoinOutstanding(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
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

        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var durableWorkerExistence = new DurableWorkerExistenceBatcher(
            persistence,
            WorkerObservationPollInterval);
        var pending = outstanding.ToDictionary(
            static workerId => workerId,
            workerId => this.WaitForWorkerCompletion(
                run,
                session,
                workerId,
                pendingCancellation.Token,
                durableWorkerExistence.WorkerExists));
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
                var remainingAfterCompletion = remaining - 1;
                if (remainingAfterCompletion > 0 &&
                    (remainingAfterCompletion & (remainingAfterCompletion - 1)) == 0)
                {
                    await this.UpsertRun(run, persistenceGate, CancellationToken.None);
                }

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

    private async Task<WorkflowRunCompletion> WaitForOutstandingWorkers(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        IWorkSystemSession session,
        IReadOnlyList<WorkerId> workerIds,
        CancellationToken cancellationToken)
    {
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var durableWorkerExistence = new DurableWorkerExistenceBatcher(
            persistence,
            WorkerObservationPollInterval);
        var pending = workerIds
            .Distinct()
            .ToDictionary(
                static workerId => workerId,
                workerId => this.WaitForWorkerCompletion(
                    run,
                    session,
                    workerId,
                    pendingCancellation.Token,
                    durableWorkerExistence.WorkerExists));
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
        IWorkSystemSession session,
        WorkerId workerId,
        CancellationToken cancellationToken,
        Func<WorkerId, CancellationToken, Task<bool>>? durableWorkerExists = null)
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

        if (!await WorkerExists(workerId, durableWorkerExists, cancellationToken))
        {
            if (run.TryGetChildReceipt(workerId, out receipt) && receipt is not null)
            {
                return WorkflowExecutionSupport.FromReceipt(receipt);
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

            if (!await WorkerExists(workerId, durableWorkerExists, cancellationToken))
            {
                if (run.TryGetChildReceipt(workerId, out receipt) && receipt is not null)
                {
                    return WorkflowExecutionSupport.FromReceipt(receipt);
                }

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

    private Task<bool> WorkerExists(
        WorkerId workerId,
        Func<WorkerId, CancellationToken, Task<bool>>? durableWorkerExists,
        CancellationToken cancellationToken)
        => durableWorkerExists is null
            ? persistence.DurableWorkerExists(workerId, cancellationToken)
            : durableWorkerExists(workerId, cancellationToken);

    private WorkflowRunCompletion CreateFailedRunCompletion(
        WorkflowRunState run,
        IReadOnlyList<WorkMessage> messages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return run.CreateFinalCompletion(WorkflowRunStatus.Failed, messages);
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

    private async Task<WorkflowRunCompletion> CompleteFromChildOutcome(
        WorkflowRunState run,
        WorkflowRunCompletion completion,
        WorkflowRunPersistenceGate persistenceGate,
        CancellationToken cancellationToken)
    {
        if (completion.Status == WorkflowRunStatus.Blocked)
        {
            return await this.UpsertBlockedRun(run, completion.Messages, persistenceGate, cancellationToken);
        }

        if (completion.Status == WorkflowRunStatus.Canceled)
        {
            return new WorkflowRunCompletion(
                WorkflowRunStatus.Canceled,
                null,
                completion.Messages,
                CancelOutstandingChildren: true);
        }

        return this.CreateFailedRunCompletion(run, completion.Messages, cancellationToken);
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

    private sealed class DurableWorkerExistenceBatcher : IAsyncDisposable
    {
        private readonly Lock sync = new();
        private readonly WorkflowPersistenceCoordinator persistence;
        private readonly TimeSpan interval;
        private readonly SemaphoreSlim signal = new(0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Dictionary<WorkerId, List<TaskCompletionSource<bool>>> pending = [];
        private readonly Task pump;

        public DurableWorkerExistenceBatcher(
            WorkflowPersistenceCoordinator persistence,
            TimeSpan interval)
        {
            this.persistence = persistence;
            this.interval = interval;
            this.pump = this.Run();
        }

        public Task<bool> WorkerExists(
            WorkerId workerId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shouldSignal = false;
            lock (this.sync)
            {
                if (!this.pending.TryGetValue(workerId, out var requests))
                {
                    requests = [];
                    this.pending[workerId] = requests;
                }

                shouldSignal = this.pending.Count == 1 && requests.Count == 0;
                requests.Add(completion);
            }

            if (shouldSignal)
            {
                this.signal.Release();
            }

            return completion.Task.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            this.cancellation.Cancel();
            try
            {
                await this.pump;
            }
            catch (OperationCanceledException) when (this.cancellation.IsCancellationRequested)
            {
                // Cancellation is expected while stopping the background pump.
            }

            this.signal.Dispose();
            this.cancellation.Dispose();
        }

        private async Task Run()
        {
            try
            {
                while (true)
                {
                    await this.signal.WaitAsync(this.cancellation.Token);
                    await Task.Delay(this.interval, this.cancellation.Token);

                    Dictionary<WorkerId, List<TaskCompletionSource<bool>>> batch;
                    lock (this.sync)
                    {
                        batch = new Dictionary<WorkerId, List<TaskCompletionSource<bool>>>(this.pending);
                        this.pending.Clear();
                    }

                    try
                    {
                        var existing = await this.persistence.DurableWorkersExist(
                            batch.Keys,
                            this.cancellation.Token);
                        foreach (var item in batch)
                        {
                            var workerExists = existing.Contains(item.Key);
                            foreach (var completion in item.Value)
                            {
                                completion.TrySetResult(workerExists);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (this.cancellation.IsCancellationRequested)
                    {
                        foreach (var completion in batch.Values.SelectMany(static requests => requests))
                        {
                            completion.TrySetCanceled(this.cancellation.Token);
                        }

                        throw;
                    }
                    catch (OperationCanceledException exception)
                    {
                        SetBatchException(batch, exception);
                    }
                    catch (Exception exception) when (ShouldHandleExecutionException(exception))
                    {
                        SetBatchException(batch, exception);
                    }
                    catch (Exception exception)
                        when (!ShouldHandleExecutionException(exception) &&
                            exception is not OperationCanceledException)
                    {
                        SetBatchException(batch, exception);
                        throw;
                    }
                }
            }
            finally
            {
                List<TaskCompletionSource<bool>> abandoned;
                lock (this.sync)
                {
                    abandoned = [.. this.pending.Values.SelectMany(static requests => requests)];
                    this.pending.Clear();
                }

                foreach (var completion in abandoned)
                {
                    completion.TrySetCanceled(this.cancellation.Token);
                }
            }
        }

        private static void SetBatchException(
            IReadOnlyDictionary<WorkerId, List<TaskCompletionSource<bool>>> batch,
            Exception exception)
        {
            foreach (var completion in batch.Values.SelectMany(static requests => requests))
            {
                completion.TrySetException(exception);
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

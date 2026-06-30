namespace Workable;

internal sealed class DurableWorkflowExecutor(
    string workSystemKey,
    Func<string, RegisteredWork?> getRegisteredWork,
    Func<WorkRequestContext, IWorkSystemSession> createSession,
    Func<WorkerId, IWorkerHandle> createWorkerHandle,
    WorkflowPersistenceCoordinator persistence)
{
    public Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
        => this.Execute(run, workflow, null, null, cancellationToken);

    public async Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        Func<WorkflowStepDefinition, bool>? shouldStopBeforeStep = null,
        Func<bool>? shouldStopAfterOutstanding = null,
        CancellationToken cancellationToken = default)
    {
        IWorkSystemSession? session = null;
        shouldStopBeforeStep ??= static _ => false;
        shouldStopAfterOutstanding ??= static () => false;
        try
        {
            run.MarkRunning();
            session = createSession(run.RequestContext);

            foreach (var step in workflow.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shouldStopBeforeStep(step))
                {
                    break;
                }

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
                                return await this.DeleteFailedRun(run, messages, cancellationToken);
                            }

                            break;
                        }
                    case ParallelWorkflowStepDefinition parallel:
                        {
                            var messages = await this.DispatchParallel(run, session, parallel, cancellationToken);
                            if (messages.Count > 0)
                            {
                                run.FailStep(parallel.Name, messages);
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
                            }

                            var completion = await this.WaitForJoinOutstanding(run, session, join.Name, cancellationToken);
                            if (!completion.IsCompletedSuccessfully)
                            {
                                await WorkflowExecutionSupport.CancelOutstandingChildren(run, session, cancellationToken);
                                run.FailStep(join.Name, completion.Messages);
                                return await this.DeleteFailedRun(run, completion.Messages, cancellationToken);
                            }

                            run.MarkStepCompleted(join.Name);
                            await persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
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
                session,
                run.GetOutstandingWorkerIds(),
                cancellationToken);
            if (!trailingCompletion.IsCompletedSuccessfully)
            {
                await WorkflowExecutionSupport.CancelOutstandingChildren(run, session, cancellationToken);
                return await this.DeleteFailedRun(run, trailingCompletion.Messages, cancellationToken);
            }

            if (shouldStopAfterOutstanding())
            {
                return run.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var success = run.Complete();
            await persistence.DeleteRun(run.Id, CancellationToken.None);
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return run.Cancel();
        }
        catch (Exception exception)
        {
            return await this.DeleteFailedRun(
                run,
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")],
                cancellationToken);
        }
    }

    private async Task<IReadOnlyList<WorkMessage>> Dispatch(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        var registeredWork = getRegisteredWork(step.WorkDefinitionName)
            ?? throw new InvalidOperationException($"Workflow step '{step.Name}' targets unknown work '{step.WorkDefinitionName}'.");
        run.MarkStepRunning(step.Name);

        try
        {
            await persistence.ExecuteTransaction(
                async (transaction, transactionOptions, transactionCancellationToken) =>
                {
                    var handle = await session.Queue.Enqueue(
                            step.WorkDefinitionName,
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

            return [];
        }
        catch (WorkflowDispatchRejectedException rejection)
        {
            return rejection.Messages;
        }
    }

    private async Task<IReadOnlyList<WorkMessage>> DispatchParallel(
        WorkflowRunState run,
        IWorkSystemSession session,
        ParallelWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        run.MarkStepRunning(step.Name);

        try
        {
            await persistence.ExecuteTransaction(
                async (transaction, transactionOptions, transactionCancellationToken) =>
                {
                    var workerIds = new List<WorkerId>();
                    foreach (var child in step.Steps.OfType<DispatchWorkflowStepDefinition>())
                    {
                        var registeredWork = getRegisteredWork(child.WorkDefinitionName)
                            ?? throw new InvalidOperationException($"Workflow step '{child.Name}' targets unknown work '{child.WorkDefinitionName}'.");
                        var handle = await session.Queue.Enqueue(
                            child.WorkDefinitionName,
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
            var completion = await this.WaitForWorkerCompletion(session, workerId, cancellationToken);
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
        }

        return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
    }

    private async Task<WorkflowRunCompletion> WaitForOutstandingWorkers(
        IWorkSystemSession session,
        IReadOnlyList<WorkerId> workerIds,
        CancellationToken cancellationToken)
    {
        foreach (var workerId in workerIds.Distinct())
        {
            var completion = await this.WaitForWorkerCompletion(session, workerId, cancellationToken);
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
        IWorkSystemSession session,
        WorkerId workerId,
        CancellationToken cancellationToken)
    {
        if (await session.Query.Worker(workerId, cancellationToken) is { } snapshot && snapshot.IsFinal)
        {
            var completion = new WorkCompletion(
                WorkerStateMachine.CompletionStatusFor(snapshot.State),
                snapshot,
                snapshot.Output,
                snapshot.Messages);
            ThrowIfChildCompletedUnsuccessfullyAfterWorkflowCancellation(completion, cancellationToken);
            return completion;
        }

        var handle = createWorkerHandle(workerId);
        if (await persistence.DurableWorkerExists(workerId, cancellationToken))
        {
            var completion = await handle.WaitForCompletion(cancellationToken);
            ThrowIfChildCompletedUnsuccessfullyAfterWorkflowCancellation(completion, cancellationToken);
            return completion;
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

    private static void ThrowIfChildCompletedUnsuccessfullyAfterWorkflowCancellation(
        WorkCompletion completion,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested &&
            !completion.IsCompletedSuccessfully)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task<WorkflowRunCompletion> DeleteFailedRun(
        WorkflowRunState run,
        IReadOnlyList<WorkMessage> messages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failure = run.Fail(messages);
        await persistence.DeleteRun(run.Id, CancellationToken.None);
        return failure;
    }

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

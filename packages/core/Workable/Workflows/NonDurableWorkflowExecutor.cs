namespace Workable;

internal sealed class NonDurableWorkflowExecutor(
    Func<WorkRequestContext, IWorkSystemSession> createSession)
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
        var outstanding = new List<(string StepName, IWorkerHandle Handle)>();
        shouldStopBeforeStep ??= static _ => false;
        shouldStopAfterOutstanding ??= static () => false;

        try
        {
            run.MarkRunning();
            var session = createSession(run.RequestContext);

            foreach (var step in workflow.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shouldStopBeforeStep(step))
                {
                    break;
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

                            outstanding.Add((dispatch.Name, result.Handle!));
                            break;
                        }
                    case ParallelWorkflowStepDefinition parallel:
                        {
                            run.MarkStepRunning(parallel.Name);
                            var workerIds = new List<WorkerId>();
                            foreach (var child in parallel.Steps.OfType<DispatchWorkflowStepDefinition>())
                            {
                                var input = WorkflowExecutionSupport.AddWorkflowIdentifiers(
                                    child.Input,
                                    run.Id,
                                    run.DefinitionName,
                                    child.Name);
                                var handle = await session.Queue.Enqueue(
                                    child.WorkDefinitionName,
                                    input,
                                    cancellationToken: cancellationToken);
                                if (!handle.QueueOutcome.IsAccepted)
                                {
                                    run.FailStep(parallel.Name, handle.QueueOutcome.Messages);
                                    return run.Fail(handle.QueueOutcome.Messages);
                                }

                                if (handle.WorkerId is { } childWorkerId)
                                {
                                    workerIds.Add(childWorkerId);
                                }

                                outstanding.Add((child.Name, handle));
                            }

                            run.MarkStepCompleted(parallel.Name, workerIds);
                            break;
                        }
                    case JoinWorkflowStepDefinition join:
                        {
                            run.MarkStepRunning(join.Name);
                            var completion = await WorkflowExecutionSupport.WaitForOutstanding(outstanding, cancellationToken);
                            if (!completion.IsCompletedSuccessfully)
                            {
                                run.FailStep(join.Name, completion.Messages);
                                return run.Fail(completion.Messages);
                            }

                            outstanding.Clear();
                            run.MarkStepCompleted(join.Name);
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

            if (outstanding.Count > 0)
            {
                var completion = await WorkflowExecutionSupport.WaitForOutstanding(outstanding, cancellationToken);
                if (!completion.IsCompletedSuccessfully)
                {
                    return run.Fail(completion.Messages);
                }
            }

            return shouldStopAfterOutstanding()
                ? run.Cancel()
                : run.Complete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return run.Cancel();
        }
        catch (Exception exception)
        {
            return run.Fail(
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")]);
        }
    }

    private async Task<DispatchResult> Dispatch(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        run.MarkStepRunning(step.Name);
        var input = WorkflowExecutionSupport.AddWorkflowIdentifiers(
            step.Input,
            run.Id,
            run.DefinitionName,
            step.Name);
        var handle = await session.Queue.Enqueue(step.WorkDefinitionName, input, cancellationToken: cancellationToken);
        if (!handle.QueueOutcome.IsAccepted)
        {
            run.FailStep(step.Name, handle.QueueOutcome.Messages);
            return new DispatchResult(false, null, handle.QueueOutcome.Messages);
        }

        run.MarkStepCompleted(step.Name, handle.WorkerId is { } workerId ? [workerId] : []);
        return new DispatchResult(true, handle, []);
    }

    private sealed record DispatchResult(
        bool IsAccepted,
        IWorkerHandle? Handle,
        IReadOnlyList<WorkMessage> Messages);
}

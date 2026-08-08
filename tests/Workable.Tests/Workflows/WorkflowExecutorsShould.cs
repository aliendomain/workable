using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowExecutorsShould
{
    [Fact]
    public async Task CancelNonDurableExecutionWhenCancellationIsRequested()
    {
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) => throw new NotSupportedException())));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.cancel"),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completion = await executor.Execute(run, workflow, cancellation.Token);

        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task FailNonDurableExecutionForUnsupportedStepKind()
    {
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) => throw new NotSupportedException())));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.unsupported"),
            new UnsupportedWorkflowStepDefinition("unsupported"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.step.unsupported");
    }

    [Fact]
    public async Task FailNonDurableExecutionWhenCreatingTheSessionThrows()
    {
        var executor = new NonDurableWorkflowExecutor(_ => throw new InvalidOperationException("session failed"));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.exception"),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.execution_exception");
    }

    [Fact]
    public async Task FailNonDurableExecutionWhenParallelDispatchIsRejected()
    {
        var queueCalls = 0;
        WorkerId? acceptedWorkerId = null;
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
            {
                if (Interlocked.Increment(ref queueCalls) == 2)
                {
                    return Task.FromResult<IWorkerHandle>(RejectedHandle(WorkQueueOutcome.Invalid(
                        [WorkMessage.Error("workflow.parallel.rejected", $"Rejected {name}.")])));
                }

                acceptedWorkerId = WorkerId.New();
                return Task.FromResult<IWorkerHandle>(AcceptedHandle(acceptedWorkerId.Value));
            })));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.parallel.reject"),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.parallel.rejected");
        Assert.Equal(WorkflowStepRunStatus.Failed, run.ToSnapshot().Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Equal(2, queueCalls);
        Assert.NotNull(acceptedWorkerId);
    }

    [Fact]
    public async Task FailNonDurableBranchWhenChildDispatchIsRejected()
    {
        var queueCalls = 0;
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
                Task.FromResult<IWorkerHandle>(
                    Interlocked.Increment(ref queueCalls) == 2
                        ? RejectedHandle(WorkQueueOutcome.Invalid(
                            [WorkMessage.Error("workflow.branch.rejected", $"Rejected {name}.")]))
                        : AcceptedHandle(WorkerId.New())))));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.branch.reject"),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                new BranchWorkflowStepDefinition("documents",
                [
                    Dispatch("alpha", "sample.alpha"),
                    Dispatch("beta", "sample.beta"),
                ]),
            ]));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);
        var snapshot = run.ToSnapshot();

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.branch.rejected");
        Assert.Equal(WorkflowStepRunStatus.Failed, snapshot.Steps.Single(step => step.Name == "documents").Status);
        Assert.Equal(WorkflowStepRunStatus.Failed, snapshot.Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Equal(2, queueCalls);
    }

    [Fact]
    public async Task FailNonDurableExecutionWhenJoinObservesCanceledChild()
    {
        var alphaId = WorkerId.New();
        var betaId = WorkerId.New();
        var queueCalls = 0;
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
            {
                var workerId = Interlocked.Increment(ref queueCalls) == 1 ? alphaId : betaId;
                return Task.FromResult<IWorkerHandle>(
                    workerId == alphaId
                        ? AcceptedHandle(workerId)
                        : new TestWorkerHandle(
                            WorkQueueOutcome.Accepted(workerId),
                            workerId,
                            Task.FromResult(new WorkCompletion(WorkCompletionStatus.Canceled, null, null, []))));
            })));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.join.canceled"),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);
        var snapshot = run.ToSnapshot();

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Equal(WorkflowStepRunStatus.Running, snapshot.Steps.Single(step => step.Name == "join").Status);
    }

    [Theory]
    [InlineData(WorkCompletionStatus.NotFound, "workflow.child.not_found")]
    [InlineData(WorkCompletionStatus.Invalid, "workflow.child.invalid")]
    public async Task FailNonDurableExecutionWhenJoinObservesInvalidChildCompletion(
        WorkCompletionStatus childStatus,
        string messageCode)
    {
        var alphaId = WorkerId.New();
        var betaId = WorkerId.New();
        var queueCalls = 0;
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
            {
                var workerId = Interlocked.Increment(ref queueCalls) == 1 ? alphaId : betaId;
                return Task.FromResult<IWorkerHandle>(
                    workerId == alphaId
                        ? AcceptedHandle(workerId)
                        : new TestWorkerHandle(
                            WorkQueueOutcome.Accepted(workerId),
                            workerId,
                            Task.FromResult(new WorkCompletion(
                                childStatus,
                                null,
                                null,
                                [WorkMessage.Error(messageCode, "Child did not complete successfully.")]))));
            })));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.join.invalid"),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);
        var snapshot = run.ToSnapshot();

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Equal(WorkflowStepRunStatus.Failed, snapshot.Steps.Single(step => step.Name == "join").Status);
        Assert.Contains(completion.Messages, message => message.Code == messageCode);
    }

    [Fact]
    public async Task FailNonDurableExecutionWhenTrailingChildIsCanceledWithoutJoin()
    {
        var workerId = WorkerId.New();
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(WorkCompletionStatus.Canceled, null, null, [])))))));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.trailing.canceled"),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Equal(WorkflowStepRunStatus.Completed, run.ToSnapshot().Steps.Single(step => step.Name == "dispatch").Status);
    }

    [Fact]
    public async Task FailNonDurableExecutionWhenTrailingChildFailsWithoutJoin()
    {
        var workerId = WorkerId.New();
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(
                        WorkCompletionStatus.Failed,
                        null,
                        null,
                        [WorkMessage.Error("workflow.trailing.failed", "Trailing child failed.")])))))));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.trailing.failed"),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.trailing.failed");
    }

    [Fact]
    public async Task CompleteNonDurableExecutionWhenAcceptedDispatchDoesNotExposeAWorkerId()
    {
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(WorkerId.New()),
                    null,
                    Task.FromResult(new WorkCompletion(WorkCompletionStatus.Completed, null, null, [])))))));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.null-worker.single"),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);
        var step = run.ToSnapshot().Steps.Single();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Empty(step.WorkerIds);
    }

    [Fact]
    public async Task CompleteNonDurableExecutionWhenParallelDispatchDoesNotExposeWorkerIds()
    {
        var queueCalls = 0;
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
            {
                Interlocked.Increment(ref queueCalls);
                return Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(WorkerId.New()),
                    null,
                    Task.FromResult(new WorkCompletion(WorkCompletionStatus.Completed, null, null, []))));
            })));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.null-worker.parallel"),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);
        var dispatch = run.ToSnapshot().Steps.Single(step => step.Name == "dispatch");

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(2, queueCalls);
        Assert.Empty(dispatch.WorkerIds);
    }

    [Fact]
    public async Task FailNonDurableExecutionWhenDispatchEachSourceOutputIsNotArray()
    {
        var loadWorkerId = WorkerId.New();
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
                Task.FromResult<IWorkerHandle>(
                    string.Equals(name, "sample.load", StringComparison.Ordinal)
                        ? new TestWorkerHandle(
                            WorkQueueOutcome.Accepted(loadWorkerId),
                            loadWorkerId,
                            Task.FromResult(new WorkCompletion(
                                WorkCompletionStatus.Completed,
                                null,
                                WorkOutput.FromJson("""{"items":{"id":"alpha"}}"""),
                                [])))
                        : AcceptedHandle(WorkerId.New())))));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.dispatch-each.invalid"),
            Dispatch("load", "sample.load"),
            DispatchEach("fan-out", "load", "sample.process", "/items"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.dispatch_each.source_output_not_array");
        Assert.Equal(WorkflowStepRunStatus.Failed, run.ToSnapshot().Steps.Single(step => step.Name == "fan-out").Status);
    }

    [Fact]
    public async Task BlockNonDurableExecutionWhenDispatchEachSourceIsPaused()
    {
        var loadWorkerId = WorkerId.New();
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(loadWorkerId),
                    loadWorkerId,
                    Task.FromResult(new WorkCompletion(
                        WorkCompletionStatus.Paused,
                        null,
                        null,
                        [WorkMessage.Warning("workflow.source.paused", "Source paused.")])))))));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.dispatch-each.source-paused"),
            Dispatch("load", "sample.load"),
            DispatchEach("fan-out", "load", "sample.process", "/items"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.source.paused");
        Assert.Equal(WorkflowStepRunStatus.Running, run.ToSnapshot().Steps.Single(step => step.Name == "fan-out").Status);
    }

    [Fact]
    public async Task PersistJoinTransitionsAndDeleteCompletedDurableRun()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var alphaId = WorkerId.New();
        var betaId = WorkerId.New();
        var queueCalls = 0;
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
            {
                var workerId = Interlocked.Increment(ref queueCalls) == 1 ? alphaId : betaId;
                return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
            })),
            workerId => CompletedHandle(workerId),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.join",
                coordination: WorkflowCoordinationConfiguration.Durable),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "alpha").Status == WorkflowStepRunStatus.Completed);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "beta").Status == WorkflowStepRunStatus.Completed);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "dispatch").Status == WorkflowStepRunStatus.Completed);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Completed);
        Assert.Empty(store.DeletedRunIds);
    }

    [Fact]
    public async Task NotifyDurableQueueAfterDispatchTransactionCommitsBeforeWaitingForChild()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var workerId = WorkerId.New();
        var queue = new DelegateQueueService((_, _, _, _) =>
            Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId)));
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(queue),
            observedWorkerId =>
            {
                Assert.Equal(workerId, observedWorkerId);
                Assert.Equal(1, store.TransactionCommitCount);
                Assert.Equal(1, queue.DurableWorkNotifications);
                return CompletedHandle(observedWorkerId);
            },
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.post-commit-notification",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, queue.DurableWorkNotifications);
    }

    [Fact]
    public async Task PersistRemainingJoinWorkersAsChildrenComplete()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var alphaId = WorkerId.New();
        var betaId = WorkerId.New();
        var queueCalls = 0;
        var betaCompletion = new TaskCompletionSource<WorkCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
            {
                var workerId = Interlocked.Increment(ref queueCalls) == 1 ? alphaId : betaId;
                return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
            })),
            workerId => workerId == alphaId
                ? CompletedHandle(workerId)
                : new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    betaCompletion.Task),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.join.progress",
                coordination: WorkflowCoordinationConfiguration.Durable),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        using var cancellation = new CancellationTokenSource();

        var executeTask = executor.Execute(run, workflow, cancellation.Token);
        await TestEventually.Until(
            () => store.Upserts.Any(record =>
                record.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running &&
                record.Steps.Single(step => step.Name == "join").WorkerIds.Count == 1),
            "Expected the durable join step to persist the remaining worker after one child completed.");
        cancellation.Cancel();
        betaCompletion.TrySetCanceled(cancellation.Token);
        var completion = await executeTask;

        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
        Assert.Contains(
            store.Upserts,
            record => record.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running &&
                record.Steps.Single(step => step.Name == "join").WorkerIds.Count == 2);
        Assert.Contains(
            store.Upserts,
            record => record.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running &&
                record.Steps.Single(step => step.Name == "join").WorkerIds.SequenceEqual([betaId]));
    }

    [Fact]
    public async Task CheckpointLargeJoinProgressWithoutOneWritePerChild()
    {
        const int childCount = 128;
        var workerIds = Enumerable.Range(0, childCount).Select(_ => WorkerId.New()).ToArray();
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new InvalidOperationException("No dispatch expected."))),
            CompletedHandle,
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.join.scaling",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", workerIds);
        run.MarkStepRunning("join", workerIds);

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        var progressWrites = store.Upserts.Count(record =>
            record.Steps.Single(step => step.Name == "join") is
            {
                Status: WorkflowStepRunStatus.Running,
                WorkerIds: { Count: > 0 },
            });
        Assert.InRange(progressWrites, 1, 8);
    }

    [Fact]
    public async Task ReturnFailedDurableCandidateForUnsupportedStepKindWithoutCommittingIt()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) => throw new NotSupportedException())),
            CompletedHandle,
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.unsupported",
                coordination: WorkflowCoordinationConfiguration.Durable),
            new UnsupportedWorkflowStepDefinition("unsupported"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.step.unsupported");
        Assert.Equal(WorkflowRunStatus.Running, run.ToSnapshot().Status);
        Assert.Empty(store.DeletedRunIds);
        Assert.DoesNotContain(store.Upserts, record => record.Status == WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task ReturnFailedDurableCandidateWhenSingleDispatchIsRejectedWithoutCommittingIt()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
                Task.FromResult<IWorkerHandle>(RejectedHandle(WorkQueueOutcome.Invalid(
                    [WorkMessage.Error("workflow.dispatch.rejected", $"Rejected {name}.")]))))),
            CompletedHandle,
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.dispatch.reject",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.dispatch.rejected");
        Assert.Equal(WorkflowStepRunStatus.Failed, run.ToSnapshot().Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Equal(WorkflowRunStatus.Running, run.ToSnapshot().Status);
        Assert.Empty(store.DeletedRunIds);
        Assert.DoesNotContain(store.Upserts, record => record.Status == WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task ReturnFailedDurableCandidateWhenParallelDispatchIsRejectedWithoutCommittingIt()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var queueCalls = 0;
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
            {
                if (Interlocked.Increment(ref queueCalls) == 2)
                {
                    return Task.FromResult<IWorkerHandle>(RejectedHandle(WorkQueueOutcome.Invalid(
                        [WorkMessage.Error("workflow.dispatch.rejected", $"Rejected {name}.")])));
                }

                return Task.FromResult<IWorkerHandle>(AcceptedHandle(WorkerId.New()));
            })),
            CompletedHandle,
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.parallel.reject",
                coordination: WorkflowCoordinationConfiguration.Durable),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.dispatch.rejected");
        Assert.Equal(WorkflowRunStatus.Running, run.ToSnapshot().Status);
        Assert.Empty(store.DeletedRunIds);
        Assert.DoesNotContain(store.Upserts, record => record.Status == WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task PersistFailedDurableBranchWhenChildDispatchIsRejected()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var queueCalls = 0;
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
                Task.FromResult<IWorkerHandle>(
                    Interlocked.Increment(ref queueCalls) == 2
                        ? RejectedHandle(WorkQueueOutcome.Invalid(
                            [WorkMessage.Error("workflow.branch.rejected", $"Rejected {name}.")]))
                        : AcceptedHandle(WorkerId.New())))),
            workerId => CompletedHandle(workerId),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.branch.reject",
                coordination: WorkflowCoordinationConfiguration.Durable),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                new BranchWorkflowStepDefinition("documents",
                [
                    Dispatch("alpha", "sample.alpha"),
                    Dispatch("beta", "sample.beta"),
                ]),
            ]));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);
        var snapshot = run.ToSnapshot();

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.branch.rejected");
        Assert.Equal(WorkflowStepRunStatus.Failed, snapshot.Steps.Single(step => step.Name == "documents").Status);
        Assert.Equal(WorkflowStepRunStatus.Failed, snapshot.Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "documents").Status == WorkflowStepRunStatus.Failed);
        Assert.Contains(store.Upserts, record => record.Steps.Single(step => step.Name == "dispatch").Status == WorkflowStepRunStatus.Failed);
        Assert.Equal(2, queueCalls);
    }

    [Fact]
    public async Task PersistBlockedDurableRunWhenDispatchEachSourceIsPaused()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var loadWorkerId = WorkerId.New();
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(AcceptedHandle(loadWorkerId)))),
            workerId => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(workerId),
                workerId,
                Task.FromResult(new WorkCompletion(
                    WorkCompletionStatus.Paused,
                    null,
                    null,
                    [WorkMessage.Warning("workflow.source.paused", "Source paused.")]))),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.dispatch-each.source-paused",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            DispatchEach("fan-out", "load", "sample.process", "/items"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.source.paused");
        Assert.Equal(WorkflowStepRunStatus.Running, run.ToSnapshot().Steps.Single(step => step.Name == "fan-out").Status);
        Assert.Equal(WorkflowRunStatus.Blocked, store.Upserts.Last().Status);
    }

    [Theory]
    [InlineData(WorkflowCanceledChildBehavior.Continue, WorkflowRunStatus.Completed)]
    [InlineData(WorkflowCanceledChildBehavior.Block, WorkflowRunStatus.Blocked)]
    [InlineData(WorkflowCanceledChildBehavior.CancelWorkflow, WorkflowRunStatus.Canceled)]
    public async Task ApplyDispatchEachCanceledChildBehaviorInDurableExecution(
        WorkflowCanceledChildBehavior canceledChildBehavior,
        WorkflowRunStatus expectedStatus)
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var loadWorkerId = WorkerId.New();
        var expandedWorkerIds = new List<WorkerId>();
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
            {
                var workerId = string.Equals(name, "sample.load", StringComparison.Ordinal)
                    ? loadWorkerId
                    : WorkerId.New();
                if (workerId != loadWorkerId)
                {
                    expandedWorkerIds.Add(workerId);
                }

                return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
            })),
            workerId => workerId == loadWorkerId
                ? new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(
                        WorkCompletionStatus.Completed,
                        null,
                        WorkOutput.FromJson("""{"items":[{"id":"alpha"},{"id":"beta"}]}"""),
                        [])))
                : new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(
                        workerId == expandedWorkerIds[0]
                            ? WorkCompletionStatus.Canceled
                            : WorkCompletionStatus.Completed,
                        null,
                        null,
                        []))),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                $"workflow.durable.dispatch-each.canceled.{canceledChildBehavior}".ToLowerInvariant(),
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            DispatchEach(
                "fan-out",
                "load",
                "sample.process",
                "/items",
                canceledChildBehavior),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(expectedStatus, completion.Status);
        Assert.Equal(2, expandedWorkerIds.Count);
        Assert.Equal(
            canceledChildBehavior == WorkflowCanceledChildBehavior.CancelWorkflow,
            completion.CancelOutstandingChildren);
        if (expectedStatus == WorkflowRunStatus.Blocked)
        {
            Assert.Equal(expectedStatus, store.Upserts.Last().Status);
        }
        else if (expectedStatus == WorkflowRunStatus.Canceled)
        {
            Assert.Equal(WorkflowRunStatus.Running, run.GetStatus());
            Assert.DoesNotContain(store.Upserts, item => item.Status == WorkflowRunStatus.Canceled);
        }
    }

    [Theory]
    [InlineData(WorkflowCanceledChildBehavior.Continue, WorkflowRunStatus.Completed)]
    [InlineData(WorkflowCanceledChildBehavior.Block, WorkflowRunStatus.Blocked)]
    [InlineData(WorkflowCanceledChildBehavior.CancelWorkflow, WorkflowRunStatus.Canceled)]
    public async Task ApplyDispatchEachCanceledChildBehaviorInNonDurableExecution(
        WorkflowCanceledChildBehavior canceledChildBehavior,
        WorkflowRunStatus expectedStatus)
    {
        var expandedWorkerCount = 0;
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
            {
                var workerId = WorkerId.New();
                if (string.Equals(name, "sample.load", StringComparison.Ordinal))
                {
                    return Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                        WorkQueueOutcome.Accepted(workerId),
                        workerId,
                        Task.FromResult(new WorkCompletion(
                            WorkCompletionStatus.Completed,
                            null,
                            WorkOutput.FromJson("""{"items":[{"id":"alpha"},{"id":"beta"}]}"""),
                            []))));
                }

                var completionStatus = Interlocked.Increment(ref expandedWorkerCount) == 1
                    ? WorkCompletionStatus.Canceled
                    : WorkCompletionStatus.Completed;
                return Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(completionStatus, null, null, []))));
            })));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                $"workflow.non-durable.dispatch-each.canceled.{canceledChildBehavior}".ToLowerInvariant()),
            Dispatch("load", "sample.load"),
            DispatchEach(
                "fan-out",
                "load",
                "sample.process",
                "/items",
                canceledChildBehavior),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(expectedStatus, completion.Status);
        Assert.Equal(2, expandedWorkerCount);
        Assert.Equal(
            canceledChildBehavior == WorkflowCanceledChildBehavior.CancelWorkflow,
            completion.CancelOutstandingChildren);
    }

    [Theory]
    [InlineData(WorkflowCanceledChildBehavior.Continue, WorkflowRunStatus.Completed)]
    [InlineData(WorkflowCanceledChildBehavior.Block, WorkflowRunStatus.Blocked)]
    [InlineData(WorkflowCanceledChildBehavior.CancelWorkflow, WorkflowRunStatus.Canceled)]
    public async Task ApplyCanceledSourceBehaviorWhenChainingNonDurableDispatchEachOutputs(
        WorkflowCanceledChildBehavior canceledChildBehavior,
        WorkflowRunStatus expectedStatus)
    {
        var processCount = 0;
        var gatheredInputs = new List<string>();
        var executor = new NonDurableWorkflowExecutor(
            _ => new TestWorkSystemSession(new DelegateQueueService((name, input, _, _) =>
            {
                var workerId = WorkerId.New();
                var completion = name switch
                {
                    "sample.load" => new WorkCompletion(
                        WorkCompletionStatus.Completed,
                        null,
                        WorkOutput.FromJson("""{"items":[{"id":"alpha"},{"id":"beta"}]}"""),
                        []),
                    "sample.process" when Interlocked.Increment(ref processCount) == 1 => new WorkCompletion(
                        WorkCompletionStatus.Canceled,
                        null,
                        null,
                        []),
                    "sample.process" => new WorkCompletion(
                        WorkCompletionStatus.Completed,
                        null,
                        WorkOutput.FromJson("""{"items":[{"id":"beta-artifact"}]}"""),
                        []),
                    "sample.gather" => RecordGatheredInput(input, gatheredInputs),
                    _ => throw new InvalidOperationException($"Unexpected work definition '{name}'."),
                };
                return Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(completion)));
            })));
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.non-durable.chained-canceled-continue"),
            Dispatch("load", "sample.load"),
            DispatchEach(
                "process",
                "load",
                "sample.process",
                "/items",
                canceledChildBehavior),
            DispatchEach("gather", "process", "sample.gather", "/items"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(expectedStatus, completion.Status);
        if (canceledChildBehavior == WorkflowCanceledChildBehavior.Continue)
        {
            Assert.Equal(["""{"id":"beta-artifact"}"""], gatheredInputs);
        }
        else
        {
            Assert.Empty(gatheredInputs);
        }

        Assert.Equal(
            canceledChildBehavior == WorkflowCanceledChildBehavior.CancelWorkflow,
            completion.CancelOutstandingChildren);
    }

    [Theory]
    [InlineData(WorkflowCanceledChildBehavior.Continue, WorkflowRunStatus.Completed)]
    [InlineData(WorkflowCanceledChildBehavior.Block, WorkflowRunStatus.Blocked)]
    [InlineData(WorkflowCanceledChildBehavior.CancelWorkflow, WorkflowRunStatus.Canceled)]
    public async Task ApplyCanceledSourceBehaviorWhenChainingDurableDispatchEachOutputs(
        WorkflowCanceledChildBehavior canceledChildBehavior,
        WorkflowRunStatus expectedStatus)
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var processCount = 0;
        var gatheredInputs = new List<string>();
        var completions = new Dictionary<WorkerId, WorkCompletion>();
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            CreateRegisteredWork,
            _ => new TestWorkSystemSession(new DelegateQueueService((name, input, _, _) =>
            {
                var workerId = WorkerId.New();
                completions[workerId] = name switch
                {
                    "sample.load" => new WorkCompletion(
                        WorkCompletionStatus.Completed,
                        null,
                        WorkOutput.FromJson("""{"items":[{"id":"alpha"},{"id":"beta"}]}"""),
                        []),
                    "sample.process" when Interlocked.Increment(ref processCount) == 1 => new WorkCompletion(
                        WorkCompletionStatus.Canceled,
                        null,
                        null,
                        []),
                    "sample.process" => new WorkCompletion(
                        WorkCompletionStatus.Completed,
                        null,
                        WorkOutput.FromJson("""{"items":[{"id":"beta-artifact"}]}"""),
                        []),
                    "sample.gather" => RecordGatheredInput(input, gatheredInputs),
                    _ => throw new InvalidOperationException($"Unexpected work definition '{name}'."),
                };
                return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
            })),
            workerId => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(workerId),
                workerId,
                Task.FromResult(completions[workerId])),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.chained-canceled-continue",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            DispatchEach(
                "process",
                "load",
                "sample.process",
                "/items",
                canceledChildBehavior),
            DispatchEach("gather", "process", "sample.gather", "/items"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(expectedStatus, completion.Status);
        if (canceledChildBehavior == WorkflowCanceledChildBehavior.Continue)
        {
            Assert.Equal(["""{"id":"beta-artifact"}"""], gatheredInputs);
        }
        else
        {
            Assert.Empty(gatheredInputs);
        }

        Assert.Equal(
            canceledChildBehavior == WorkflowCanceledChildBehavior.CancelWorkflow,
            completion.CancelOutstandingChildren);
        if (expectedStatus == WorkflowRunStatus.Blocked)
        {
            Assert.Equal(expectedStatus, store.Upserts.Last().Status);
        }
        else if (expectedStatus == WorkflowRunStatus.Canceled)
        {
            Assert.Equal(WorkflowRunStatus.Running, run.GetStatus());
            Assert.DoesNotContain(store.Upserts, item => item.Status == WorkflowRunStatus.Canceled);
        }
    }

    [Fact]
    public async Task CancelRemainingDurableJoinWaitersWhenOneChildBlocksTheWorkflow()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var processCount = 0;
        var pendingWaitCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerHandles = new Dictionary<WorkerId, IWorkerHandle>();
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            CreateRegisteredWork,
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
            {
                var workerId = WorkerId.New();
                workerHandles[workerId] = name switch
                {
                    "sample.load" => new TestWorkerHandle(
                        WorkQueueOutcome.Accepted(workerId),
                        workerId,
                        Task.FromResult(new WorkCompletion(
                            WorkCompletionStatus.Completed,
                            null,
                            WorkOutput.FromJson("""{"items":[{"id":"alpha"},{"id":"beta"}]}"""),
                            []))),
                    "sample.process" when Interlocked.Increment(ref processCount) == 1 => new TestWorkerHandle(
                        WorkQueueOutcome.Accepted(workerId),
                        workerId,
                        Task.FromResult(new WorkCompletion(WorkCompletionStatus.Canceled, null, null, []))),
                    "sample.process" => new CancellationAwareWorkerHandle(workerId, pendingWaitCanceled),
                    _ => throw new InvalidOperationException($"Unexpected work definition '{name}'."),
                };
                return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
            })),
            workerId => workerHandles[workerId],
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.cancel-pending-join-waits",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            DispatchEach(
                "fan-out",
                "load",
                "sample.process",
                "/items",
                WorkflowCanceledChildBehavior.Block),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        await pendingWaitCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkflowRunStatus.Blocked, store.Upserts.Last().Status);
    }

    [Fact]
    public async Task CancelRemainingDurableTrailingWaitersWhenOneChildBlocksTheWorkflow()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var processCount = 0;
        var pendingWaitCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerHandles = new Dictionary<WorkerId, IWorkerHandle>();
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            CreateRegisteredWork,
            _ => new TestWorkSystemSession(new DelegateQueueService((name, _, _, _) =>
            {
                var workerId = WorkerId.New();
                workerHandles[workerId] = name switch
                {
                    "sample.load" => new TestWorkerHandle(
                        WorkQueueOutcome.Accepted(workerId),
                        workerId,
                        Task.FromResult(new WorkCompletion(
                            WorkCompletionStatus.Completed,
                            null,
                            WorkOutput.FromJson("""{"items":[{"id":"alpha"},{"id":"beta"}]}"""),
                            []))),
                    "sample.process" when Interlocked.Increment(ref processCount) == 1 => new TestWorkerHandle(
                        WorkQueueOutcome.Accepted(workerId),
                        workerId,
                        Task.FromResult(new WorkCompletion(WorkCompletionStatus.Canceled, null, null, []))),
                    "sample.process" => new CancellationAwareWorkerHandle(workerId, pendingWaitCanceled),
                    _ => throw new InvalidOperationException($"Unexpected work definition '{name}'."),
                };
                return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
            })),
            workerId => workerHandles[workerId],
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.cancel-pending-trailing-waits",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            DispatchEach(
                "fan-out",
                "load",
                "sample.process",
                "/items",
                WorkflowCanceledChildBehavior.Block));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        await pendingWaitCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkflowRunStatus.Blocked, store.Upserts.Last().Status);
    }

    [Fact]
    public async Task DeleteDurableRunWhenJoinObservesFailedChildCompletion()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var workerId = WorkerId.New();
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId)))),
            _ => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(workerId),
                workerId,
                Task.FromResult(new WorkCompletion(
                    WorkCompletionStatus.Failed,
                    null,
                    null,
                    [WorkMessage.Error("workflow.child.failed", "Child failed.")]))),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.join.failure",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.child.failed");
        Assert.Equal(WorkflowStepRunStatus.Running, run.ToSnapshot().Steps.Single(step => step.Name == "join").Status);
        Assert.Empty(store.DeletedRunIds);
    }

    [Fact]
    public async Task CompleteRecoveredJoinFromAuthoritativeFinalWorkerSnapshotWithoutWaitingOnHandle()
    {
        var workerId = WorkerId.New();
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var handleWaits = 0;
        var finalSnapshot = CreateSnapshot(workerId, WorkerState.Completed);
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(id => Task.FromResult(id == workerId ? finalSnapshot : null))),
            id =>
            {
                Interlocked.Increment(ref handleWaits);
                return new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(id),
                    id,
                    Task.FromCanceled<WorkCompletion>(new CancellationToken(true)));
            },
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.recovered.authoritative-child",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Rehydrate(
            workflow,
            new WorkflowRunPersistenceRecord(
                "workflow-tests",
                WorkflowRunId.New(),
                workflow.Definition.Version,
                workflow.Definition.Name,
                null,
                WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                WorkflowRunStatus.Running,
                [
                    new WorkflowStepPersistenceRecord(
                        "dispatch",
                        WorkflowStepKind.DispatchWork,
                        WorkflowStepRunStatus.Completed,
                        [workerId],
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        []),
                    new WorkflowStepPersistenceRecord(
                        "join",
                        WorkflowStepKind.Join,
                        WorkflowStepRunStatus.Running,
                        [workerId],
                        DateTimeOffset.UtcNow,
                        null,
                        []),
                ],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                [],
                []));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(0, Volatile.Read(ref handleWaits));
        Assert.Empty(store.DeletedRunIds);
    }

    [Fact]
    public async Task ReturnFailedDurableCandidateWhenRecoveredChildStateIsMissingWithoutCommittingIt()
    {
        var workerId = WorkerId.New();
        var store = new RecordingWorkflowStore();
        store.MissingWorkerIds.Add(workerId);
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null))),
            _ => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(workerId),
                workerId,
                Task.FromCanceled<WorkCompletion>(new CancellationToken(true))),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.recovered.missing-child",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Rehydrate(
            workflow,
            new WorkflowRunPersistenceRecord(
                "workflow-tests",
                WorkflowRunId.New(),
                workflow.Definition.Version,
                workflow.Definition.Name,
                null,
                WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                WorkflowRunStatus.Running,
                [
                    new WorkflowStepPersistenceRecord(
                        "dispatch",
                        WorkflowStepKind.DispatchWork,
                        WorkflowStepRunStatus.Completed,
                        [workerId],
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        []),
                    new WorkflowStepPersistenceRecord(
                        "join",
                        WorkflowStepKind.Join,
                        WorkflowStepRunStatus.Running,
                        [workerId],
                        DateTimeOffset.UtcNow,
                        null,
                        []),
                ],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                [],
                []));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.child.not_found");
        Assert.Equal(WorkflowRunStatus.Running, run.ToSnapshot().Status);
        Assert.Empty(store.DeletedRunIds);
        Assert.DoesNotContain(store.Upserts, record => record.Status == WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task ObserveLargeChildCompletionSetInCompletionOrderWithOneSourceEnumeration()
    {
        const int childCount = 4096;
        var workerIds = Enumerable.Range(0, childCount).Select(_ => WorkerId.New()).ToArray();
        var completionSources = workerIds
            .Select(_ => new TaskCompletionSource<WorkCompletion>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var sourceEnumerations = 0;

        IEnumerable<(WorkerId WorkerId, Task<WorkCompletion> Completion)> PendingChildren()
        {
            Interlocked.Increment(ref sourceEnumerations);
            for (var index = 0; index < childCount; index++)
            {
                yield return (workerIds[index], completionSources[index].Task);
            }
        }

        var completions = new WorkflowChildCompletionQueue(PendingChildren());
        for (var index = childCount - 1; index >= 0; index--)
        {
            completionSources[index].SetResult(
                new WorkCompletion(WorkCompletionStatus.Completed, null, null, []));

            var completed = await completions.ReadAsync(CancellationToken.None);

            Assert.Equal(workerIds[index], completed.WorkerId);
            Assert.Equal(WorkCompletionStatus.Completed, completed.Completion.Status);
        }

        Assert.Equal(1, Volatile.Read(ref sourceEnumerations));
    }

    [Fact]
    public async Task PropagateFaultFromChildCompletionQueue()
    {
        var expected = new InvalidOperationException("Child wait failed.");
        var completions = new WorkflowChildCompletionQueue(
            [(WorkerId.New(), Task.FromException<WorkCompletion>(expected))]);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await completions.ReadAsync(CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task BatchDurableExistenceChecksForLargeOutstandingFanOuts()
    {
        var workerIds = Enumerable.Range(0, 128).Select(_ => WorkerId.New()).ToArray();
        var store = new RecordingWorkflowStore
        {
            DurableWorkersExistHandler = _ => new HashSet<WorkerId>(),
        };
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null))),
            workerId => new CancellationAwareWorkerHandle(
                workerId,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.batched-child-existence",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", workerIds);
        run.MarkStepRunning("join", workerIds);

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        var batch = Assert.Single(store.DurableWorkerExistenceBatches);
        Assert.Equal(workerIds.OrderBy(static id => id.Value), batch.OrderBy(static id => id.Value));
        Assert.Equal(0, store.DurableWorkerExistenceCalls);
    }

    [Fact]
    public async Task CompleteActiveExistenceBatchWhenProviderThrowsOperationCanceledException()
    {
        var workerId = WorkerId.New();
        var store = new RecordingWorkflowStore
        {
            DurableWorkersExistHandler = _ => throw new OperationCanceledException("Provider canceled its lookup."),
        };
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null))),
            id => new CancellationAwareWorkerHandle(
                id,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.provider-canceled-existence",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [workerId]);
        run.MarkStepRunning("join", [workerId]);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await executor.Execute(run, workflow, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CompleteActiveExistenceBatchWhenProviderThrowsCriticalException()
    {
        var workerId = WorkerId.New();
        var store = new RecordingWorkflowStore
        {
            DurableWorkersExistHandler = _ => throw new BadImageFormatException("Critical existence lookup failure."),
        };
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null))),
            id => new CancellationAwareWorkerHandle(
                id,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.critical-existence-failure",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [workerId]);
        run.MarkStepRunning("join", [workerId]);

        var exception = await Assert.ThrowsAsync<BadImageFormatException>(async () =>
            await executor.Execute(run, workflow, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("Critical existence lookup failure.", exception.Message);
    }

    [Theory]
    [InlineData(true, WorkCompletionStatus.Completed)]
    [InlineData(false, WorkCompletionStatus.NotFound)]
    public async Task RecheckAuthoritativeStateWhenDurableChildStateDisappearsDuringObservation(
        bool finalSnapshotAppears,
        WorkCompletionStatus expectedStatus)
    {
        var workerId = WorkerId.New();
        var store = new RecordingWorkflowStore();
        var durableChecks = 0;
        store.DurableWorkerExistsHandler = _ => Interlocked.Increment(ref durableChecks) == 1;
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var snapshotReads = 0;
        var running = CreateSnapshot(workerId, WorkerState.Running);
        var completed = CreateSnapshot(workerId, WorkerState.Completed);
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) => throw new NotSupportedException())),
            id => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(id),
                id,
                new TaskCompletionSource<WorkCompletion>(TaskCreationOptions.RunContinuationsAsynchronously).Task),
            persistence,
            getAuthoritativeWorker: (_, _) =>
            {
                var read = Interlocked.Increment(ref snapshotReads);
                return Task.FromResult<WorkerSnapshot?>(read <= 2
                    ? running
                    : finalSnapshotAppears ? completed : null);
            });
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.child-disappears",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var method = typeof(DurableWorkflowExecutor).GetMethod(
            "WaitForWorkerCompletion",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            executor,
            [run, new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) => throw new NotSupportedException())), workerId, CancellationToken.None, null]));
        await task;
        var completion = Assert.IsType<WorkCompletion>(task.GetType().GetProperty("Result")!.GetValue(task));

        Assert.Equal(expectedStatus, completion.Status);
        Assert.Equal(2, Volatile.Read(ref durableChecks));
        Assert.Equal(3, Volatile.Read(ref snapshotReads));
        if (finalSnapshotAppears)
        {
            Assert.Same(completed, completion.Worker);
        }
        else
        {
            Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.child.not_found");
        }
    }

    [Fact]
    public async Task CancelOutstandingSiblingWorkersWhenJoinFails()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var alphaId = WorkerId.New();
        var betaId = WorkerId.New();
        var queueCalls = 0;
        var canceledWorkers = new List<WorkerVersion>();
        var querySnapshots = new Dictionary<WorkerId, WorkerSnapshot>
        {
            [alphaId] = CreateSnapshot(alphaId, WorkerState.Failed),
            [betaId] = CreateSnapshot(betaId, WorkerState.Running),
        };
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) =>
                {
                    var workerId = Interlocked.Increment(ref queueCalls) == 1 ? alphaId : betaId;
                    return Task.FromResult<IWorkerHandle>(AcceptedHandle(workerId));
                }),
                workers: new RecordingWorkerOperations(canceledWorkers),
                query: new DelegateQueryService(id => Task.FromResult(querySnapshots.GetValueOrDefault(id)))),
            workerId => workerId == alphaId
                ? new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(
                        WorkCompletionStatus.Failed,
                        querySnapshots[workerId],
                        null,
                        [WorkMessage.Error("workflow.child.failed", "Child failed.")])))
                : new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromCanceled<WorkCompletion>(new CancellationToken(true))),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.join.cancel-siblings",
                coordination: WorkflowCoordinationConfiguration.Durable),
            new ParallelWorkflowStepDefinition("dispatch",
            [
                Dispatch("alpha", "sample.alpha"),
                Dispatch("beta", "sample.beta"),
            ]),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var completion = await executor.Execute(run, workflow, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
        Assert.Empty(canceledWorkers);
        Assert.Empty(store.DeletedRunIds);
    }

    [Fact]
    public async Task CancelDurableExecutionWhenCancellationIsRequested()
    {
        var store = new RecordingWorkflowStore();
        var persistence = new WorkflowPersistenceCoordinator(store, "workflow-tests");
        var executor = new DurableWorkflowExecutor(
            "workflow-tests",
            name => CreateRegisteredWork(name),
            _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) => Task.FromResult<IWorkerHandle>(AcceptedHandle(WorkerId.New())))),
            workerId => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(workerId),
                workerId,
                Task.FromCanceled<WorkCompletion>(new CancellationToken(true))),
            persistence);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.cancel",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completion = await executor.Execute(run, workflow, cancellation.Token);

        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
    }

    private static RegisteredWorkflow CreateWorkflow(
        WorkflowDefinition definition,
        params WorkflowStepDefinition[] steps)
        => new(
            definition,
            steps,
            WorkOperateAuthorizationConfiguration.None);

    private static DispatchWorkflowStepDefinition Dispatch(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null)
        => new(stepName, WorkDefinition.Create(workDefinitionName), input);

    private static DispatchEachWorkflowStepDefinition DispatchEach(
        string stepName,
        string sourceStepName,
        string workDefinitionName,
        string? sourceJsonPointer = null,
        WorkflowCanceledChildBehavior canceledChildBehavior = WorkflowCanceledChildBehavior.Continue)
        => new(
            stepName,
            new WorkflowStepReference<object?>(sourceStepName),
            WorkDefinition.Create(workDefinitionName),
            new WorkflowOutputSelector(sourceJsonPointer),
            canceledChildBehavior);

    private static RegisteredWork CreateRegisteredWork(string name)
        => new(
            WorkDefinition.Create(name),
            _ => throw new NotSupportedException(),
            []);

    private static IWorkerHandle AcceptedHandle(WorkerId workerId)
        => new TestWorkerHandle(
            WorkQueueOutcome.Accepted(workerId),
            workerId,
            Task.FromResult(new WorkCompletion(WorkCompletionStatus.Completed, null, null, [])));

    private static IWorkerHandle CompletedHandle(WorkerId workerId)
        => new TestWorkerHandle(
            WorkQueueOutcome.Accepted(workerId),
            workerId,
            Task.FromResult(new WorkCompletion(WorkCompletionStatus.Completed, null, null, [])));

    private static IWorkerHandle RejectedHandle(WorkQueueOutcome outcome)
        => new TestWorkerHandle(
            outcome,
            null,
            Task.FromResult(new WorkCompletion(WorkCompletionStatus.Invalid, null, null, outcome.Messages)));

    private static WorkCompletion RecordGatheredInput(
        WorkInput? input,
        List<string> gatheredInputs)
    {
        gatheredInputs.Add(input?.Json ?? throw new InvalidOperationException("Expected gathered input."));
        return new WorkCompletion(WorkCompletionStatus.Completed, null, null, []);
    }

    private static WorkerSnapshot CreateSnapshot(WorkerId workerId, WorkerState state)
        => new(
            workerId,
            Revision: 1,
            StateSequence: 1,
            DefinitionName: "sample.dispatch",
            DefinitionCategory: string.Empty,
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            RequestContext: WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            State: state,
            Input: null,
            Output: null,
            Options: WorkerOptions.Default,
            Configuration: WorkConfiguration.Default,
            Messages: [],
            InterruptionReason: null,
            CreatedAt: DateTimeOffset.UtcNow,
            StateChangedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed record UnsupportedWorkflowStepDefinition(string StepName)
        : WorkflowStepDefinition(StepName, (WorkflowStepKind)999);

    private sealed class TestWorkSystemSession(
        IWorkQueueService queue,
        IWorkerOperations? workers = null,
        IWorkQueryService? query = null) : IWorkSystemSession
    {
        public string? SystemName => "workflow-tests";

        public WorkSystemState SystemState => WorkSystemState.Started;

        public WorkSystemCapabilities Capabilities => WorkSystemCapabilities.None;

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue { get; } = queue;

        public IWorkerOperations Workers { get; } = workers ?? new RecordingWorkerOperations([]);

        public IWorkQueryService Query { get; } = query ?? new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null));

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();
    }

    private sealed class DelegateQueueService(
        Func<string, WorkInput?, WorkerOptions?, CancellationToken, Task<IWorkerHandle>> enqueue)
        : IWorkQueueService
    {
        public int DurableWorkNotifications { get; private set; }

        public void NotifyDurableWorkAvailable()
            => this.DurableWorkNotifications++;

        public Task<IWorkerHandle> Enqueue(
            string name,
            WorkInput? input = null,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
            => enqueue(name, input, options, cancellationToken);

        public Task<IWorkerHandle> Enqueue<TInput>(
            string name,
            TInput input,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
            => enqueue(name, WorkInput.FromValue(input), options, cancellationToken);
    }

    private sealed class RecordingWorkflowStore : IWorkPersistenceStore
    {
        public List<WorkflowRunPersistenceRecord> Upserts { get; } = [];

        public List<WorkflowRunId> DeletedRunIds { get; } = [];

        public HashSet<WorkerId> MissingWorkerIds { get; } = [];

        public Func<WorkerId, bool>? DurableWorkerExistsHandler { get; set; }

        public Func<IReadOnlyCollection<WorkerId>, IReadOnlySet<WorkerId>>? DurableWorkersExistHandler { get; set; }

        public List<IReadOnlyList<WorkerId>> DurableWorkerExistenceBatches { get; } = [];

        public int DurableWorkerExistenceCalls { get; private set; }

        public int TransactionCommitCount { get; private set; }

        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task RenewLeases(
            IReadOnlyList<WorkQueueDurabilityLease> leases,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RetainFailed(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
            => this.DeleteFinal(workers, cancellationToken);

        public Task<bool> DurableWorkerExists(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
        {
            this.DurableWorkerExistenceCalls++;
            return Task.FromResult(this.DurableWorkerExistsHandler?.Invoke(workerId) ?? !this.MissingWorkerIds.Contains(workerId));
        }

        public Task<IReadOnlySet<WorkerId>> DurableWorkersExist(
            IReadOnlyCollection<WorkerId> workerIds,
            CancellationToken cancellationToken = default)
        {
            this.DurableWorkerExistenceBatches.Add([.. workerIds]);
            if (this.DurableWorkersExistHandler is not null)
            {
                return Task.FromResult(this.DurableWorkersExistHandler(workerIds));
            }

            IReadOnlySet<WorkerId> existing = workerIds
                .Where(workerId => this.DurableWorkerExistsHandler?.Invoke(workerId) ?? !this.MissingWorkerIds.Contains(workerId))
                .ToHashSet();
            return Task.FromResult(existing);
        }

        public Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
            WorkflowPersistenceTransactionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IWorkflowPersistenceTransaction>(new RecordingWorkflowTransaction(
                () => this.TransactionCommitCount++));

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
        {
            this.Upserts.Add(run);
            return Task.CompletedTask;
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            this.Upserts.Add(run);
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            this.DeletedRunIds.Add(request.RunId);
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            this.DeletedRunIds.Add(request.RunId);
            return Task.CompletedTask;
        }

        private sealed class RecordingWorkflowTransaction(Action onCommit) : IWorkflowPersistenceTransaction
        {
            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

            public Task Commit(CancellationToken cancellationToken = default)
            {
                onCommit();
                return Task.CompletedTask;
            }
        }
    }

    private sealed class TestWorkerHandle(
        WorkQueueOutcome queueOutcome,
        WorkerId? workerId,
        Task<WorkCompletion> completion)
        : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId { get; } = workerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => completion;

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
    }

    private sealed class CancellationAwareWorkerHandle(
        WorkerId workerId,
        TaskCompletionSource waitCanceled) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = WorkQueueOutcome.Accepted(workerId);

        public WorkerId? WorkerId { get; } = workerId;

        public async Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Expected the pending child wait to be canceled.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                waitCanceled.TrySetResult();
                throw;
            }
        }

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
    }

    private sealed class DelegateQueryService(
        Func<WorkerId, Task<WorkerSnapshot?>> worker)
        : IWorkQueryService
    {
        public Task<WorkerSnapshot?> Worker(WorkerId workerId, CancellationToken cancellationToken = default)
            => worker(workerId);

        public Task<WorkerIterationSnapshot?> WorkerIteration(WorkerIterationReference iteration, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerQueryResult> Workers(WorkerCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerIterationQueryResult> WorkerIterations(WorkerIterationCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkInfo?> WorkInfo(string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkDefinitionQueryResult> WorkDefinitions(WorkDefinitionCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerKeyQueryResult> WorkerKeys(WorkerKeyCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(WorkerKeyTypeCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkIterationKeyQueryResult> WorkIterationKeys(WorkIterationKeyCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(WorkIterationKeyTypeCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerStatusSummary> WorkerStatusSummary(WorkerCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemDetails> SystemDetails(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemThroughput> SystemThroughput(WorkSystemCriteria? criteria = null, WorkThroughputCriteria? throughput = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemThroughputSummary> SystemThroughputSummary(WorkSystemCriteria? criteria = null, WorkThroughputCriteria? throughput = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemWorkerCounts> SystemWorkerCounts(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemIterationCounts> SystemIterationCounts(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemFailedWorkers> SystemFailedWorkers(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(WorkSystemCriteria? criteria = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingWorkerOperations(List<WorkerVersion> canceledWorkers) : IWorkerOperations
    {
        public Task<WorkActionOutcome> Execute(WorkerVersion worker, WorkAction action, CancellationToken cancellationToken = default)
        {
            if (action == WorkAction.Cancel)
            {
                canceledWorkers.Add(worker);
            }

            return Task.FromResult(WorkActionOutcome.Accepted(action, CreateSnapshot(worker.WorkerId, WorkerState.Canceled), []));
        }

        public Task<WorkActionOutcome> Execute(WorkerVersion worker, WorkerActionRequest request, CancellationToken cancellationToken = default)
            => this.Execute(worker, request.Action, cancellationToken);

        public Task<WorkerBulkActionOutcome> ExecuteAll(WorkAction action, WorkerBulkActionFilter? filter = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkActionOutcome> Reconfigure(WorkerVersion worker, WorkerReconfiguration changes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

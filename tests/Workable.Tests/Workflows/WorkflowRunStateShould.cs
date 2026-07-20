using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRunStateShould
{
    [Theory]
    [InlineData(WorkflowRunStatus.Completed)]
    [InlineData(WorkflowRunStatus.Failed)]
    [InlineData(WorkflowRunStatus.Canceled)]
    public void StageFinalCompletionUntilItIsCommitted(WorkflowRunStatus finalStatus)
    {
        var workflow = CreateWorkflow(WorkflowDefinition.Create("workflow.staged-completion"));
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        IReadOnlyList<WorkMessage> messages = finalStatus == WorkflowRunStatus.Failed
            ? new[] { WorkMessage.Error("workflow.failed", "Expected failure.") }
            : [];

        var completion = run.CreateFinalCompletion(
            finalStatus,
            messages,
            cancelOutstandingChildren: finalStatus == WorkflowRunStatus.Canceled);
        var visibleBeforeCommit = run.ToSnapshot();
        var ordinaryPersistence = run.ToPersistenceRecord("workflow-tests");
        var stagedPersistence = run.ToPersistenceRecord("workflow-tests", completion);

        Assert.Equal(WorkflowRunStatus.Running, visibleBeforeCommit.Status);
        Assert.Null(visibleBeforeCommit.CompletedAt);
        Assert.Equal(WorkflowRunStatus.Running, ordinaryPersistence.Status);
        Assert.Null(ordinaryPersistence.CompletedAt);
        Assert.Equal(finalStatus, completion.Status);
        Assert.Equal(finalStatus, stagedPersistence.Status);
        Assert.NotNull(stagedPersistence.CompletedAt);
        Assert.Equal(messages, stagedPersistence.Messages);

        var committed = run.CommitFinalCompletion(completion);

        Assert.Equal(finalStatus, committed.Status);
        Assert.Equal(finalStatus, run.ToSnapshot().Status);
        Assert.Equal(finalStatus == WorkflowRunStatus.Canceled, committed.CancelOutstandingChildren);
        Assert.Equal(finalStatus, run.CommitFinalCompletion(completion).Status);
    }

    [Fact]
    public void RejectNonFinalStagedCompletions()
    {
        var workflow = CreateWorkflow(WorkflowDefinition.Create("workflow.invalid-staged-completion"));
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var nonFinal = new WorkflowRunCompletion(WorkflowRunStatus.Blocked, run.ToSnapshot(), []);

        Assert.Throws<ArgumentOutOfRangeException>(() => run.CreateFinalCompletion(WorkflowRunStatus.Blocked));
        Assert.Throws<ArgumentException>(() => run.CommitFinalCompletion(nonFinal));
        Assert.Throws<ArgumentException>(() => run.ToPersistenceRecord("workflow-tests", nonFinal));
    }

    [Fact]
    public void IncludeDispatchEachCanceledChildBehaviorInDefinitionFingerprint()
    {
        var definition = WorkflowDefinition.Create("workflow.dispatch-each.fingerprint");
        var source = new WorkflowStepReference<object?>("load");
        var selector = new WorkflowOutputSelector("/items");
        var continueWorkflow = CreateWorkflow(
            definition,
            Dispatch("load", "sample.load"),
            new DispatchEachWorkflowStepDefinition(
                "fan-out",
                source,
                WorkDefinition.Create("sample.process"),
                selector,
                WorkflowCanceledChildBehavior.Continue));
        var blockWorkflow = CreateWorkflow(
            definition,
            Dispatch("load", "sample.load"),
            new DispatchEachWorkflowStepDefinition(
                "fan-out",
                source,
                WorkDefinition.Create("sample.process"),
                selector,
                WorkflowCanceledChildBehavior.Block));

        Assert.NotEqual(
            WorkflowDefinitionFingerprint.Create(continueWorkflow),
            WorkflowDefinitionFingerprint.Create(blockWorkflow));
    }

    [Fact]
    public void TrackOutstandingWorkersAcrossJoinBoundaries()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.outstanding"),
            Dispatch("prepare", "sample.prepare"),
            new ParallelWorkflowStepDefinition("notify",
            [
                Dispatch("email", "sample.email"),
                Dispatch("invoice", "sample.invoice"),
            ]),
            new JoinWorkflowStepDefinition("join-1"),
            Dispatch("archive", "sample.archive"),
            new JoinWorkflowStepDefinition("join-2"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var first = WorkerId.New();
        var second = WorkerId.New();
        var third = WorkerId.New();
        var fourth = WorkerId.New();

        run.MarkStepCompleted("prepare", [first]);
        run.MarkStepCompleted("notify", [second, third]);
        Assert.Equal([first, second, third], run.GetOutstandingWorkerIds());

        run.MarkStepCompleted("join-1");
        Assert.Empty(run.GetOutstandingWorkerIds());

        run.MarkStepCompleted("archive", [fourth]);
        Assert.Equal([fourth], run.GetOutstandingWorkerIds());
    }

    [Fact]
    public void MaintainStepWorkerMembershipAcrossStateChangesAndRehydration()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.worker-membership"),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var alpha = WorkerId.New();
        var beta = WorkerId.New();

        run.MarkStepCompleted("dispatch", [alpha, beta]);

        Assert.True(run.StepContainsWorker("dispatch", alpha));
        Assert.True(run.StepContainsWorker("dispatch", beta));
        Assert.False(run.StepContainsWorker("dispatch", WorkerId.New()));

        run.RemoveStepWorkerId("dispatch", alpha);

        Assert.False(run.StepContainsWorker("dispatch", alpha));
        Assert.True(run.StepContainsWorker("dispatch", beta));

        var rehydrated = WorkflowRunState.Rehydrate(
            workflow,
            run.ToPersistenceRecord("workflow-tests"));

        Assert.False(rehydrated.StepContainsWorker("dispatch", alpha));
        Assert.True(rehydrated.StepContainsWorker("dispatch", beta));
    }

    [Fact]
    public void RehydrateUsesDefinitionOrderAndRestoresPersistedStepState()
    {
        var definition = WorkflowDefinition.Create("workflow.rehydrate");
        var workflow = CreateWorkflow(
            definition,
            Dispatch("prepare", "sample.prepare"),
            new JoinWorkflowStepDefinition("join"),
            Dispatch("archive", "sample.archive"));
        var runId = WorkflowRunId.New();
        var workerId = WorkerId.New();
        var input = WorkInput.FromValue(new RehydrateInput("rehydrate-42"));
        var record = new WorkflowRunPersistenceRecord(
            "workflow-tests",
            runId,
            definition.Version,
            definition.Name,
            input,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkflowRunStatus.Running,
            [
                new WorkflowStepPersistenceRecord(
                    "archive",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Pending,
                    [],
                    null,
                    null,
                    []),
                new WorkflowStepPersistenceRecord(
                    "prepare",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [workerId],
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    []),
                new WorkflowStepPersistenceRecord(
                    "join",
                    WorkflowStepKind.Join,
                    WorkflowStepRunStatus.Running,
                    [],
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    [WorkMessage.Info("workflow.join.waiting", "Waiting.")]),
            ],
            DateTimeOffset.UtcNow.AddMinutes(-3),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            null,
            [],
            []);

        var run = WorkflowRunState.Rehydrate(workflow, record);
        var snapshot = run.ToSnapshot();
        var persisted = run.ToPersistenceRecord("workflow-tests");

        Assert.Equal(runId, snapshot.Id);
        Assert.Equal(input.Json, snapshot.Input?.Json);
        Assert.Equal(["prepare", "join", "archive"], snapshot.Steps.Select(step => step.Name).ToArray());
        Assert.Equal(WorkflowStepRunStatus.Completed, snapshot.Steps[0].Status);
        Assert.Equal([workerId], snapshot.Steps[0].WorkerIds);
        Assert.Equal(WorkflowStepRunStatus.Running, snapshot.Steps[1].Status);
        Assert.Equal(WorkflowStepRunStatus.Pending, snapshot.Steps[2].Status);
        Assert.Equal(WorkflowDefinitionFingerprint.Create(workflow), persisted.DefinitionFingerprint);
        Assert.Equal(input.Json, persisted.Input?.Json);
    }

    [Fact]
    public void RehydrateCreatesDefaultStepStateWhenNoPersistenceRecordExistsForAStep()
    {
        var definition = WorkflowDefinition.Create("workflow.rehydrate.missing-step");
        var workflow = CreateWorkflow(
            definition,
            Dispatch("prepare", "sample.prepare"),
            new JoinWorkflowStepDefinition("join"));
        var record = new WorkflowRunPersistenceRecord(
            "workflow-tests",
            WorkflowRunId.New(),
            definition.Version,
            definition.Name,
            null,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkflowRunStatus.Running,
            [
                new WorkflowStepPersistenceRecord(
                    "prepare",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [WorkerId.New()],
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow,
                    []),
            ],
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null,
            [],
            []);

        var run = WorkflowRunState.Rehydrate(workflow, record);
        var join = run.ToSnapshot().Steps.Single(step => step.Name == "join");

        Assert.Equal(WorkflowStepRunStatus.Pending, join.Status);
        Assert.Empty(join.WorkerIds);
    }

    [Fact]
    public void FailStepSetsStartedAtWhenTheStepHadNotStartedYet()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.fail-step"),
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        run.FailStep("dispatch", [WorkMessage.Error("workflow.dispatch.failed", "Dispatch failed.")]);

        var step = run.ToSnapshot().Steps.Single();
        Assert.Equal(WorkflowStepRunStatus.Failed, step.Status);
        Assert.NotNull(step.StartedAt);
        Assert.NotNull(step.CompletedAt);
        Assert.Contains(step.Messages, message => message.Code == "workflow.dispatch.failed");
    }

    [Fact]
    public void PersistAndRehydratePendingControlAction()
    {
        var definition = WorkflowDefinition.Create(
            "workflow.pending-control",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.True(run.TryRecordAcceptedControlAction(WorkflowAction.Pause, out _));
        var persisted = run.ToPersistenceRecord("workflow-tests");
        var rehydrated = WorkflowRunState.Rehydrate(workflow, persisted);

        Assert.Equal(WorkflowAction.Pause.ToString(), persisted.PendingControlAction);
        Assert.Equal(WorkflowAction.Pause, rehydrated.GetPendingControlAction());
    }

    [Fact]
    public void PersistAndRehydrateChildReceipts()
    {
        var definition = WorkflowDefinition.Create(
            "workflow.child-receipts",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var workerId = WorkerId.New();

        run.MarkStepCompleted("dispatch", [workerId]);
        Assert.True(run.RecordChildReceipt(new WorkflowChildReceipt(
            workerId,
            "dispatch",
            "sample.dispatch",
            WorkerState.Completed,
            DateTimeOffset.UtcNow,
            [WorkMessage.Info("workflow.child.completed", "Child completed.")],
            WorkOutput.Empty)));

        var persisted = run.ToPersistenceRecord("workflow-tests");
        var rehydrated = WorkflowRunState.Rehydrate(workflow, persisted);

        var receipt = Assert.Single(rehydrated.GetChildReceipts());
        Assert.Equal(workerId, receipt.WorkerId);
        Assert.Equal("dispatch", receipt.StepName);
        Assert.Equal("sample.dispatch", receipt.DefinitionName);
        Assert.Equal(WorkCompletionStatus.Completed, receipt.CompletionStatus);
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

    private sealed record RehydrateInput(string Value);
}

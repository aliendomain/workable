using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRunStateShould
{
    [Fact]
    public void TrackOutstandingWorkersAcrossJoinBoundaries()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.outstanding"),
            new DispatchWorkflowStepDefinition("prepare", "sample.prepare"),
            new ParallelWorkflowStepDefinition("notify",
            [
                new DispatchWorkflowStepDefinition("email", "sample.email"),
                new DispatchWorkflowStepDefinition("invoice", "sample.invoice"),
            ]),
            new JoinWorkflowStepDefinition("join-1"),
            new DispatchWorkflowStepDefinition("archive", "sample.archive"),
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
    public void RehydrateUsesDefinitionOrderAndRestoresPersistedStepState()
    {
        var definition = WorkflowDefinition.Create("workflow.rehydrate");
        var workflow = CreateWorkflow(
            definition,
            new DispatchWorkflowStepDefinition("prepare", "sample.prepare"),
            new JoinWorkflowStepDefinition("join"),
            new DispatchWorkflowStepDefinition("archive", "sample.archive"));
        var runId = WorkflowRunId.New();
        var workerId = WorkerId.New();
        var record = new WorkflowRunPersistenceRecord(
            WorkSystemId.New(),
            "workflow-tests",
            runId,
            definition.Version,
            definition.Name,
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
            []);

        var run = WorkflowRunState.Rehydrate(workflow, record);
        var snapshot = run.ToSnapshot();
        var persisted = run.ToPersistenceRecord(WorkSystemId.New(), "workflow-tests");

        Assert.Equal(runId, snapshot.Id);
        Assert.Equal(["prepare", "join", "archive"], snapshot.Steps.Select(step => step.Name).ToArray());
        Assert.Equal(WorkflowStepRunStatus.Completed, snapshot.Steps[0].Status);
        Assert.Equal([workerId], snapshot.Steps[0].WorkerIds);
        Assert.Equal(WorkflowStepRunStatus.Running, snapshot.Steps[1].Status);
        Assert.Equal(WorkflowStepRunStatus.Pending, snapshot.Steps[2].Status);
        Assert.Equal(WorkflowDefinitionFingerprint.Create(workflow), persisted.DefinitionFingerprint);
    }

    [Fact]
    public void RehydrateCreatesDefaultStepStateWhenNoPersistenceRecordExistsForAStep()
    {
        var definition = WorkflowDefinition.Create("workflow.rehydrate.missing-step");
        var workflow = CreateWorkflow(
            definition,
            new DispatchWorkflowStepDefinition("prepare", "sample.prepare"),
            new JoinWorkflowStepDefinition("join"));
        var record = new WorkflowRunPersistenceRecord(
            WorkSystemId.New(),
            "workflow-tests",
            WorkflowRunId.New(),
            definition.Version,
            definition.Name,
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
            new DispatchWorkflowStepDefinition("dispatch", "sample.dispatch"));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        run.FailStep("dispatch", [WorkMessage.Error("workflow.dispatch.failed", "Dispatch failed.")]);

        var step = run.ToSnapshot().Steps.Single();
        Assert.Equal(WorkflowStepRunStatus.Failed, step.Status);
        Assert.NotNull(step.StartedAt);
        Assert.NotNull(step.CompletedAt);
        Assert.Contains(step.Messages, message => message.Code == "workflow.dispatch.failed");
    }

    private static RegisteredWorkflow CreateWorkflow(
        WorkflowDefinition definition,
        params WorkflowStepDefinition[] steps)
        => new(
            definition,
            steps,
            WorkOperateAuthorizationConfiguration.None);
}

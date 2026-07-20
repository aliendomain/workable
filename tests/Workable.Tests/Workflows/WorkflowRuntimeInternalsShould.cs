using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRuntimeInternalsShould
{
    [Fact]
    public async Task RecoverDurableRunsSkipsMismatchedRecordsAndDuplicateRunIds()
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var mismatchedRun = CreatePersistedRun(
            "different-system",
            WorkflowRunId.New(),
            definition,
            workerId);
        var recoveredRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            workerId);
        var store = new RawWorkflowPersistenceStore([mismatchedRun, recoveredRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: id => new PendingWorkerHandle(id));

        await runtime.RecoverDurableRuns(CancellationToken.None);
        await TestEventually.Until(
            () => runtime.Get(recoveredRun.RunId) is not null,
            "Expected the matching durable run to be rehydrated.");

        await runtime.RecoverDurableRuns(CancellationToken.None);

        Assert.Null(runtime.Get(mismatchedRun.RunId));
        Assert.NotNull(runtime.Get(recoveredRun.RunId));
        Assert.Equal(2, store.ListCalls);

        runtime.CancelExecutionLifetime();
    }

    [Fact]
    public async Task RecoverDurableRunsSkipsMissingAndNonDurableDefinitions()
    {
        var durableWorkerId = WorkerId.New();
        var nonDurableWorkerId = WorkerId.New();
        var missingWorkerId = WorkerId.New();
        var durableDefinition = WorkflowDefinition.Create(
            "workflow.durable.recover.valid",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var nonDurableDefinition = WorkflowDefinition.Create("workflow.durable.recover.non-durable");
        var durableWorkflow = CreateWorkflow(
            durableDefinition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var nonDurableWorkflow = CreateWorkflow(
            nonDurableDefinition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var missingRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            WorkflowDefinition.Create(
                "workflow.durable.recover.missing",
                coordination: WorkflowCoordinationConfiguration.Durable),
            missingWorkerId);
        var nonDurableRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            nonDurableDefinition,
            nonDurableWorkerId);
        var durableRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            durableDefinition,
            durableWorkerId);
        var store = new RawWorkflowPersistenceStore([missingRun, nonDurableRun, durableRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([durableWorkflow, nonDurableWorkflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: id => new PendingWorkerHandle(id));

        await runtime.RecoverDurableRuns(CancellationToken.None);
        await TestEventually.Until(
            () => runtime.Get(durableRun.RunId) is not null,
            "Expected the matching durable run to be rehydrated.");

        Assert.Null(runtime.Get(missingRun.RunId));
        Assert.Null(runtime.Get(nonDurableRun.RunId));
        Assert.NotNull(runtime.Get(durableRun.RunId));

        runtime.CancelExecutionLifetime();
    }

    [Fact]
    public async Task RecoverDurableRunsFailsAndDeletesRunsWhenTheDefinitionFingerprintDoesNotMatch()
    {
        var definitionId = WorkflowDefinitionId.New();
        var persistedDefinition = WorkflowDefinition.Create(
            "workflow.durable.recover.changed",
            id: definitionId,
            coordination: WorkflowCoordinationConfiguration.Durable);
        var currentDefinition = WorkflowDefinition.Create(
            persistedDefinition.Name,
            id: definitionId,
            coordination: WorkflowCoordinationConfiguration.Durable);
        var currentWorkflow = CreateWorkflow(
            currentDefinition,
            Dispatch("dispatch", "sample.dispatch"),
            Dispatch("archive", "sample.archive"),
            new JoinWorkflowStepDefinition("join"));
        var persistedRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            persistedDefinition,
            WorkerId.New());
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([currentWorkflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: id => new PendingWorkerHandle(id));

        await runtime.RecoverDurableRuns(CancellationToken.None);

        await TestEventually.Until(
            () => runtime.Get(persistedRun.RunId)?.Status == WorkflowRunStatus.Failed,
            "Expected the mismatched durable workflow run to be marked failed instead of being resumed.");
        var snapshot = runtime.Get(persistedRun.RunId)
            ?? throw new InvalidOperationException("Expected failed workflow snapshot.");

        Assert.Contains(snapshot.Messages, message => message.Code == "workable.workflow.definition_mismatch");
        Assert.Empty(store.DeletedRuns);
    }

    [Fact]
    public async Task RecoverDurableRunsResumesAndCompletesMatchingRunWithARealSession()
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.complete",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var persistedRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            workerId);
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(id => Task.FromResult(id == workerId ? CreateSnapshot(workerId, WorkerState.Completed) : null))),
            createWorkerHandle: _ => throw new InvalidOperationException("Expected authoritative recovery to avoid worker-handle waits."));

        await runtime.RecoverDurableRuns(CancellationToken.None);

        await TestEventually.Until(
            () => runtime.Get(persistedRun.RunId)?.Status == WorkflowRunStatus.Completed,
            "Expected the recovered durable workflow run to resume and complete with a real session.");
        Assert.Empty(store.DeletedRuns);
    }

    [Fact]
    public async Task RecoverDurableBlockedRunCancelsForRetainedCancelWorkflowChildReceipt()
    {
        var loadWorkerId = WorkerId.New();
        var canceledWorkerId = WorkerId.New();
        var runningWorkerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.dispatch-each-canceled",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("load", "sample.load"),
            new DispatchEachWorkflowStepDefinition(
                "fan-out",
                new WorkflowStepReference<object?>("load"),
                WorkDefinition.Create("sample.process"),
                new WorkflowOutputSelector("/items"),
                WorkflowCanceledChildBehavior.CancelWorkflow),
            new JoinWorkflowStepDefinition("join"),
            Dispatch("after-cancel", "sample.after-cancel"));
        var now = DateTimeOffset.UtcNow;
        var persistedRun = new WorkflowRunPersistenceRecord(
            "workflow-tests",
            WorkflowRunId.New(),
            definition.Version,
            definition.Name,
            null,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkflowRunStatus.Blocked,
            [
                new WorkflowStepPersistenceRecord(
                    "load",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [loadWorkerId],
                    now,
                    now,
                    []),
                new WorkflowStepPersistenceRecord(
                    "fan-out",
                    WorkflowStepKind.DispatchEach,
                    WorkflowStepRunStatus.Completed,
                    [canceledWorkerId, runningWorkerId],
                    now,
                    now,
                    []),
                new WorkflowStepPersistenceRecord(
                    "join",
                    WorkflowStepKind.Join,
                    WorkflowStepRunStatus.Running,
                    [canceledWorkerId, runningWorkerId],
                    now,
                    null,
                    []),
                new WorkflowStepPersistenceRecord(
                    "after-cancel",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Pending,
                    [],
                    null,
                    null,
                    []),
            ],
            now,
            now,
            null,
            [],
            [
                new WorkflowChildReceipt(
                    canceledWorkerId,
                    "fan-out",
                    "sample.process",
                    WorkerState.Canceled,
                    now,
                    [],
                    null),
            ],
            WorkflowDefinitionFingerprint.Create(workflow));
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var workerOperations = new BlockingRecordingWorkerOperations();
        var snapshots = new Dictionary<WorkerId, WorkerSnapshot>
        {
            [loadWorkerId] = CreateSnapshot(loadWorkerId, WorkerState.Completed),
            [canceledWorkerId] = CreateSnapshot(canceledWorkerId, WorkerState.Canceled),
            [runningWorkerId] = CreateSnapshot(runningWorkerId, WorkerState.Running),
        };
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                workers: workerOperations,
                query: new DelegateQueryService(id => Task.FromResult(snapshots.GetValueOrDefault(id)))),
            createWorkerHandle: _ => throw new InvalidOperationException("Recovery should cancel from retained worker state."));

        var recovery = runtime.RecoverDurableRuns(CancellationToken.None);
        await workerOperations.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(WorkflowRunStatus.Blocked, runtime.Get(persistedRun.RunId)?.Status);
            Assert.Empty(workerOperations.Executions);
            Assert.DoesNotContain(store.UpsertedRuns, run => run.Status == WorkflowRunStatus.Canceled);
            Assert.False(recovery.IsCompleted);
        }
        finally
        {
            workerOperations.ReleaseExecution.TrySetResult();
        }

        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        var recovered = runtime.Get(persistedRun.RunId);
        Assert.Equal(WorkflowRunStatus.Canceled, recovered!.Status);
        Assert.Contains(
            workerOperations.Executions,
            execution => execution == (runningWorkerId, WorkAction.Cancel));
        Assert.DoesNotContain(
            workerOperations.Executions,
            execution => execution.WorkerId == canceledWorkerId);
        Assert.Contains(store.UpsertedRuns, run => run.Status == WorkflowRunStatus.Canceled);
        Assert.Equal(
            WorkflowStepRunStatus.Pending,
            recovered.Steps.Single(step => step.Name == "after-cancel").Status);
    }

    [Fact]
    public async Task RecoverDurableRunIgnoresForgedCancelWorkflowChildReceipt()
    {
        var loadWorkerId = WorkerId.New();
        var legitimateChildId = WorkerId.New();
        var forgedWorkerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.recover.forged-child-receipt",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            new DispatchEachWorkflowStepDefinition(
                "fan-out",
                new WorkflowStepReference<object?>("load"),
                WorkDefinition.Create("sample.process"),
                new WorkflowOutputSelector("/items"),
                WorkflowCanceledChildBehavior.CancelWorkflow),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("load", [loadWorkerId]);
        run.MarkStepCompleted("fan-out", [legitimateChildId]);
        run.RecordChildReceipt(new WorkflowChildReceipt(
            forgedWorkerId,
            "fan-out",
            "sample.process",
            WorkerState.Canceled,
            DateTimeOffset.UtcNow,
            [],
            null));
        run.Pause();
        var store = new RawWorkflowPersistenceStore([run.ToPersistenceRecord("workflow-tests")]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new InvalidOperationException(
                "A forged retained receipt must not cancel the recovered workflow."),
            createWorkerHandle: _ => throw new NotSupportedException());

        await runtime.RecoverDurableRuns(CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Paused, runtime.Get(run.Id)!.Status);
        Assert.Empty(runtime.Get(run.Id)!.ChildReceipts);
    }

    [Fact]
    public async Task RecoverDurableRunsHonorsPersistedCancelRequestAndCancelsOutstandingChildren()
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.cancel",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var persistedRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            workerId,
            WorkflowAction.Cancel.ToString());
        var workerOperations = new BlockingRecordingWorkerOperations();
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                workers: workerOperations,
                query: new DelegateQueryService(id => Task.FromResult(id == workerId ? CreateSnapshot(workerId, WorkerState.Running) : null))),
            createWorkerHandle: id => new PendingWorkerHandle(id));

        await runtime.RecoverDurableRuns(CancellationToken.None);

        await workerOperations.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executionCompletion = runtime.WaitForExecutions(CancellationToken.None);
        try
        {
            Assert.Equal(WorkflowRunStatus.Running, runtime.Get(persistedRun.RunId)?.Status);
            Assert.Empty(workerOperations.Executions);
            Assert.DoesNotContain(store.UpsertedRuns, run => run.Status == WorkflowRunStatus.Canceled);
            Assert.False(executionCompletion.IsCompleted);
        }
        finally
        {
            workerOperations.ReleaseExecution.TrySetResult();
        }

        await executionCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(workerOperations.Executions, execution =>
            execution.WorkerId == workerId && execution.Action == WorkAction.Cancel);
        Assert.Equal(WorkflowRunStatus.Canceled, runtime.Get(persistedRun.RunId)?.Status);
        Assert.Contains(store.UpsertedRuns, run => run.Status == WorkflowRunStatus.Canceled);
        Assert.Empty(store.DeletedRuns);
    }

    [Fact]
    public async Task RecoverDurableRunsHonorsPersistedPauseRequestAndSkipsDownstreamDispatch()
    {
        var workerId = WorkerId.New();
        var dispatchedAfterJoin = 0;
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.stop",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"),
            Dispatch("archive", "sample.archive"));
        var persistedRun = new WorkflowRunPersistenceRecord(
            "workflow-tests",
            WorkflowRunId.New(),
            definition.Version,
            definition.Name,
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
                    WorkflowStepRunStatus.Pending,
                    [],
                    null,
                    null,
                    []),
                new WorkflowStepPersistenceRecord(
                    "archive",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Pending,
                    [],
                    null,
                    null,
                    []),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            [],
            WorkflowDefinitionFingerprint.Create(workflow),
            WorkflowAction.Pause.ToString());
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) =>
                {
                    Interlocked.Increment(ref dispatchedAfterJoin);
                    throw new InvalidOperationException("Recovered pause request should skip downstream dispatch.");
                }),
                query: new DelegateQueryService(id => Task.FromResult(id == workerId ? CreateSnapshot(workerId, WorkerState.Completed) : null))),
            createWorkerHandle: _ => throw new InvalidOperationException("Expected authoritative recovery to avoid worker-handle waits."));

        await runtime.RecoverDurableRuns(CancellationToken.None);

        await TestEventually.Until(
            () => runtime.Get(persistedRun.RunId)?.Status == WorkflowRunStatus.Paused,
            "Expected the recovered durable workflow run to honor the persisted pause request.",
            timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, Volatile.Read(ref dispatchedAfterJoin));
        Assert.Empty(store.DeletedRuns);
    }

    [Fact]
    public async Task RecoverDurableRunsStartsMultipleMatchingRunsForTheSameSystem()
    {
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.multiple",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var firstRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            WorkerId.New());
        var secondRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            WorkerId.New());
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: new RawWorkflowPersistenceStore([firstRun, secondRun]),
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: id => new PendingWorkerHandle(id));

        await runtime.RecoverDurableRuns(CancellationToken.None);
        await TestEventually.Until(
            () =>
            {
                var first = runtime.Get(firstRun.RunId);
                var second = runtime.Get(secondRun.RunId);
                return first is not null &&
                    second is not null &&
                    first.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running &&
                    second.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running;
            },
            "Expected all matching durable runs to be recovered and resumed.",
            timeout: TimeSpan.FromSeconds(15));

        runtime.CancelExecutionLifetime();
    }

    [Fact]
    public async Task RecoverDurableRunsPurgesFinalRunsAfterTheirChildrenDisappear()
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.final",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var persistedRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            workerId) with
        {
            Status = WorkflowRunStatus.Completed,
        };
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException(),
            getAuthoritativeWorker: (_, _) => Task.FromResult<WorkerSnapshot?>(null));

        await runtime.RecoverDurableRuns(CancellationToken.None);

        Assert.Null(runtime.Get(persistedRun.RunId));
        Assert.Equal([persistedRun.RunId], store.DeletedRuns);
    }

    [Fact]
    public async Task RecoverDurableRunsAutoResumesBlockedRunsWhoseChildrenCompletedOffline()
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.recover.blocked",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var persistedRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            workerId) with
        {
            Status = WorkflowRunStatus.Blocked,
        };
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var completedWorker = CreateSnapshot(workerId, WorkerState.Completed);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                query: new DelegateQueryService(id => Task.FromResult(id == workerId ? completedWorker : null))),
            createWorkerHandle: _ => throw new InvalidOperationException("Offline completion should be resolved authoritatively."));

        await runtime.RecoverDurableRuns(CancellationToken.None);

        await TestEventually.Until(
            () => runtime.Get(persistedRun.RunId)?.Status == WorkflowRunStatus.Completed,
            "Expected the recovered blocked run to auto-resume after confirming its child completed.");
        Assert.Contains(store.UpsertedRuns, run => run.RunId == persistedRun.RunId && run.Status == WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task KeepBlockedRunUnchangedWhenAutoResumePersistenceFails()
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.durable.auto-resume.persistence-failure",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [workerId]);
        run.Block([WorkMessage.Error("sample.blocked", "Blocked before auto-resume.")]);
        var completedWorker = CreateSnapshot(
            workerId,
            WorkerState.Completed,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", run.Id.Value.ToString("D")),
                new("workflow-step", "dispatch"),
            });
        var store = new RawWorkflowPersistenceStore(
            [],
            persisted => persisted.Status == WorkflowRunStatus.Running
                ? Task.FromException(new InvalidOperationException("auto-resume persistence failed"))
                : Task.CompletedTask);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException(),
            getAuthoritativeWorker: (id, _) => Task.FromResult<WorkerSnapshot?>(
                id == workerId ? completedWorker : null));
        GetRuns(runtime).TryAdd(run.Id, run);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.TryAutoResumeBlockedRunForCompletedWorker(workerId, CancellationToken.None));

        Assert.Equal("auto-resume persistence failed", exception.Message);
        Assert.Equal(WorkflowRunStatus.Blocked, run.GetStatus());
        Assert.Empty(GetExecutions(runtime));
        Assert.Equal(0, GetActionGateCount(runtime));
    }

    [Fact]
    public async Task IgnoreCompletedWorkersThatCannotIdentifyAWorkflowRun()
    {
        var workerId = WorkerId.New();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException(),
            getAuthoritativeWorker: (id, _) => Task.FromResult(
                id == workerId ? CreateSnapshot(workerId, WorkerState.Completed) : null));

        await runtime.TryAutoResumeBlockedRunForCompletedWorker(WorkerId.New(), CancellationToken.None);
        await runtime.TryAutoResumeBlockedRunForCompletedWorker(workerId, CancellationToken.None);

        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public async Task ClearRunsCancelsHandlesAndDisposesActiveExecutionControls()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.clear"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(workerId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var handle = runtime.Start(
            workflow.Definition.Name,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await TestEventually.Until(
            () => runtime.Get(handle.RunId!.Value)?.Steps.Single().Status == WorkflowStepRunStatus.Completed,
            "Expected the workflow to enter its pending child execution before clearing runtime state.");

        runtime.ClearRuns();
        var completion = await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
        Assert.Null(runtime.Get(handle.RunId!.Value));
    }

    [Fact]
    public async Task IgnoreUnrelatedAndNonFinalWorkersAtWorkflowRetentionBoundaries()
    {
        var childId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.worker-boundaries"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(childId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var handle = runtime.Start(
            workflow.Definition.Name,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await TestEventually.Until(
            () => runtime.Get(handle.RunId!.Value)?.Steps.Single().WorkerIds.Count == 1,
            "Expected the workflow child to be registered before exercising retention boundaries.");
        var unrelated = CreateSnapshot(WorkerId.New(), WorkerState.Completed);
        var runningChild = CreateSnapshot(
            childId,
            WorkerState.Running,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", handle.RunId!.Value.Value.ToString("D")),
                new("workflow-step", "dispatch"),
            });

        Assert.False(runtime.ShouldKeepWorkflowChildWorker(unrelated));
        await runtime.ObserveFinalWorkflowChild(unrelated, CancellationToken.None);
        await runtime.ObservePurgedWorkflowChild(unrelated, CancellationToken.None);
        await runtime.ObserveFinalWorkflowChild(runningChild, CancellationToken.None);

        Assert.True(runtime.ShouldKeepWorkflowChildWorker(runningChild));
        Assert.Equal(WorkflowRunStatus.Running, runtime.Get(handle.RunId.Value)!.Status);
        runtime.ClearRuns();
    }

    [Fact]
    public async Task AwaitInFlightDurableReceiptPersistenceForDuplicateObservation()
    {
        var childId = WorkerId.New();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.duplicate-receipt",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore(
            [],
            async _ =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task;
            });
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [childId]);
        GetRuns(runtime).TryAdd(run.Id, run);
        var child = CreateSnapshot(
            childId,
            WorkerState.Completed,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", run.Id.Value.ToString("D")),
                new("workflow-step", "dispatch"),
            });

        var firstObservation = runtime.ObserveFinalWorkflowChild(child, CancellationToken.None);
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var duplicateObservation = runtime.ObserveFinalWorkflowChild(child, CancellationToken.None);

        Assert.False(duplicateObservation.IsCompleted);
        releaseWrite.TrySetResult();
        await Task.WhenAll(firstObservation, duplicateObservation).WaitAsync(TimeSpan.FromSeconds(1));

        var persisted = Assert.Single(store.UpsertedRuns);
        Assert.Contains(persisted.ChildReceipts, receipt => receipt.WorkerId == childId);
    }

    [Fact]
    public async Task PreserveFinalDurableStateWhenAChildReceiptArrivesDuringFinalPersistence()
    {
        var childId = WorkerId.New();
        var finalWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.final-receipt-race",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore(
            [],
            async persisted =>
            {
                if (persisted.Status == WorkflowRunStatus.Completed && persisted.ChildReceipts.Count == 0)
                {
                    finalWriteEntered.TrySetResult();
                    await releaseFinalWrite.Task;
                }
            });
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [childId]);
        GetRuns(runtime).TryAdd(run.Id, run);
        var child = CreateSnapshot(
            childId,
            WorkerState.Completed,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", run.Id.Value.ToString("D")),
                new("workflow-step", "dispatch"),
            });
        var settleFinal = typeof(WorkflowRuntime).GetMethod(
            "TryPersistAndSetFinalCompletionWithActionGate",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected final workflow settlement method.");
        var finalPersistence = Assert.IsAssignableFrom<Task<bool>>(settleFinal.Invoke(
            runtime,
            [run, run.CreateFinalCompletion(WorkflowRunStatus.Completed), true]));

        await finalWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var receiptPersistence = runtime.ObserveFinalWorkflowChild(child, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(receiptPersistence.IsCompleted);

        releaseFinalWrite.TrySetResult();
        Assert.True(await finalPersistence.WaitAsync(TimeSpan.FromSeconds(1)));
        await receiptPersistence.WaitAsync(TimeSpan.FromSeconds(1));

        var persisted = store.UpsertedRuns[^1];
        Assert.Equal(WorkflowRunStatus.Completed, persisted.Status);
        Assert.Contains(persisted.ChildReceipts, receipt => receipt.WorkerId == childId);
        Assert.Equal(WorkflowRunStatus.Completed, run.GetStatus());
    }

    [Fact]
    public async Task KeepChildForRetentionAndRequestFinalizationRetryAfterReceiptPersistenceFails()
    {
        var childId = WorkerId.New();
        var attempts = 0;
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.failed-receipt-retry",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore(
            [],
            _ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("receipt persistence failed"))
                : Task.CompletedTask);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [childId]);
        GetRuns(runtime).TryAdd(run.Id, run);
        var child = CreateSnapshot(
            childId,
            WorkerState.Completed,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", run.Id.Value.ToString("D")),
                new("workflow-step", "dispatch"),
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ObserveFinalWorkflowChild(child, CancellationToken.None));

        Assert.Equal("receipt persistence failed", exception.Message);
        Assert.True(runtime.ShouldKeepWorkflowChildWorker(child));
        Assert.True(runtime.ShouldRetryWorkflowChildFinalization(child));

        await runtime.ObserveFinalWorkflowChild(child, CancellationToken.None);

        Assert.False(runtime.ShouldRetryWorkflowChildFinalization(child));
        Assert.False(runtime.ShouldKeepWorkflowChildWorker(child));
        Assert.Equal(2, attempts);
        Assert.Contains(
            store.UpsertedRuns,
            persisted => persisted.ChildReceipts.Any(receipt => receipt.WorkerId == childId));
    }

    [Fact]
    public async Task IgnoreCancelWorkflowPolicyForWorkerWithForgedWorkflowIdentifiers()
    {
        var loadWorkerId = WorkerId.New();
        var legitimateChildId = WorkerId.New();
        var forgedWorkerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.forged-child-identifiers",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("load", "sample.load"),
            new DispatchEachWorkflowStepDefinition(
                "fan-out",
                new WorkflowStepReference<object?>("load"),
                WorkDefinition.Create("sample.process"),
                new WorkflowOutputSelector("/items"),
                WorkflowCanceledChildBehavior.CancelWorkflow),
            new JoinWorkflowStepDefinition("join"));
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new InvalidOperationException(
                "A worker that is not recorded against the workflow must not cancel it."),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("load", [loadWorkerId]);
        run.MarkStepCompleted("fan-out", [legitimateChildId]);
        run.Pause();
        GetRuns(runtime).TryAdd(run.Id, run);
        var forgedWorker = CreateSnapshot(
            forgedWorkerId,
            WorkerState.Canceled,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", run.Id.Value.ToString("D")),
                new("workflow-step", "fan-out"),
            });

        await runtime.ObserveFinalWorkflowChild(forgedWorker, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Paused, runtime.Get(run.Id)!.Status);
        Assert.Empty(runtime.Get(run.Id)!.ChildReceipts);
        Assert.Empty(store.UpsertedRuns);
        Assert.False(runtime.ShouldKeepWorkflowChildWorker(forgedWorker));
    }

    [Fact]
    public async Task IgnoreForgedWorkflowChildWhenConsideringBlockedRunAutoResume()
    {
        var legitimateChildId = WorkerId.New();
        var forgedWorkerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.forged-child-auto-resume",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var run = WorkflowRunState.Create(
            workflow,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [legitimateChildId]);
        run.Block([WorkMessage.Error("workflow.child.blocked", "A child blocked the workflow.")]);
        var forgedWorker = CreateSnapshot(
            forgedWorkerId,
            WorkerState.Completed,
            new HashSet<WorkIdentifier>
            {
                new("workflow-run", run.Id.Value.ToString("D")),
                new("workflow-step", "dispatch"),
            });
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new InvalidOperationException(
                "A forged child must not resume the workflow."),
            createWorkerHandle: _ => throw new NotSupportedException(),
            getAuthoritativeWorker: (workerId, _) => Task.FromResult<WorkerSnapshot?>(
                workerId == forgedWorkerId ? forgedWorker : null));
        GetRuns(runtime).TryAdd(run.Id, run);

        await runtime.TryAutoResumeBlockedRunForCompletedWorker(forgedWorkerId, CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Blocked, runtime.Get(run.Id)!.Status);
    }

    [Theory]
    [InlineData("Cancel", WorkAction.Cancel, WorkflowRunStatus.Canceled)]
    [InlineData("Pause", WorkAction.Pause, WorkflowRunStatus.Paused)]
    public async Task InterruptActiveExecutionAndForwardControlToOutstandingChildren(
        string workflowActionName,
        WorkAction childAction,
        WorkflowRunStatus expectedStatus)
    {
        var workflowAction = Enum.Parse<WorkflowAction>(workflowActionName);
        var childId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create($"workflow.runtime.control.{workflowAction.ToString().ToLowerInvariant()}"),
            Dispatch("dispatch", "sample.dispatch"));
        var workerOperations = new RecordingWorkerOperations();
        var sessionContexts = new ConcurrentQueue<WorkRequestContext>();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: context =>
            {
                sessionContexts.Enqueue(context);
                return new TestWorkSystemSession(
                    new DelegateQueueService((_, _, _, _) =>
                        Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(childId))),
                    workers: workerOperations,
                    query: new DelegateQueryService(id => Task.FromResult(
                        id == childId ? CreateSnapshot(childId, WorkerState.Running) : null)));
            },
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var startContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("workflow-starter", "Workflow Starter"));
        var operatorContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("workflow-operator", "Workflow Operator"),
            description: "Cancel for deployment",
            isAuthenticated: true) with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                new WorkActor("workflow-operator", "Workflow Operator"),
                ["operators"],
                []),
        };
        var handle = runtime.Start(workflow.Definition.Name, startContext);
        await TestEventually.Until(
            () => runtime.Get(handle.RunId!.Value)?.Steps.Single().WorkerIds.Contains(childId) == true,
            "Expected the workflow to wait on its child before applying operator control.");

        var outcome = await runtime.Execute(handle.RunId!.Value, workflowAction, operatorContext);
        if (expectedStatus == WorkflowRunStatus.Canceled)
        {
            var completion = await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedStatus, completion.Status);
        }
        else
        {
            await TestEventually.Until(
                () => runtime.Get(handle.RunId.Value)?.Status == expectedStatus,
                "Expected the non-final workflow control state to become visible.");
        }

        Assert.True(outcome.IsAccepted);
        Assert.Equal(expectedStatus, runtime.Get(handle.RunId.Value)!.Status);
        Assert.Contains(workerOperations.Executions, execution =>
            execution.WorkerId == childId && execution.Action == childAction);
        if (workflowAction == WorkflowAction.Cancel)
        {
            Assert.Contains(sessionContexts, context =>
                context.Actor.Id == "workflow-operator" &&
                context.Description == "Cancel for deployment" &&
                context.Authorization is null);
        }
    }

    [Fact]
    public async Task RecoverPendingDurableCancellationWithItsOperatorContext()
    {
        var childId = WorkerId.New();
        var definition = WorkflowDefinition.Create(
            "workflow.runtime.recovered-cancel-context",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        var cancellationContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("recovered-operator", "Recovered Operator"),
            description: "Cancel recovered workflow",
            isAuthenticated: true);
        var persistedRun = CreatePersistedRun(
            "workflow-tests",
            WorkflowRunId.New(),
            definition,
            childId,
            WorkflowAction.Cancel.ToString(),
            cancellationContext);
        var store = new RawWorkflowPersistenceStore([persistedRun]);
        var workerOperations = new RecordingWorkerOperations();
        var sessionContexts = new ConcurrentQueue<WorkRequestContext>();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: context =>
            {
                sessionContexts.Enqueue(context);
                return new TestWorkSystemSession(
                    new DelegateQueueService((_, _, _, _) => throw new NotSupportedException()),
                    workerOperations,
                    new DelegateQueryService(id => Task.FromResult<WorkerSnapshot?>(
                        id == childId ? CreateSnapshot(childId, WorkerState.Running) : null)));
            },
            createWorkerHandle: id => new PendingWorkerHandle(id));

        await runtime.RecoverDurableRuns(CancellationToken.None);
        await TestEventually.Until(
            () => runtime.Get(persistedRun.RunId)?.Status == WorkflowRunStatus.Canceled,
            "Expected the recovered cancellation intent to settle the workflow.");

        Assert.Contains(workerOperations.Executions, execution =>
            execution.WorkerId == childId && execution.Action == WorkAction.Cancel);
        Assert.Contains(sessionContexts, context =>
            context.Actor.Id == "recovered-operator" &&
            context.Description == "Cancel recovered workflow");
    }

    [Theory]
    [InlineData("Pause")]
    [InlineData("Cancel")]
    public async Task DoNotApplyActiveControlWhenDurableIntentPersistenceFails(string actionName)
    {
        var action = Enum.Parse<WorkflowAction>(actionName);
        var childId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                $"workflow.runtime.control-persistence-{action.ToString().ToLowerInvariant()}",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore(
            [],
            persisted => persisted.PendingControlAction is not null
                ? Task.FromException(new InvalidOperationException("control intent persistence failed"))
                : Task.CompletedTask);
        var workerOperations = new RecordingWorkerOperations();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) =>
                    Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(childId))),
                workerOperations,
                new DelegateQueryService(id => Task.FromResult<WorkerSnapshot?>(
                    id == childId ? CreateSnapshot(childId, WorkerState.Running) : null))),
            createWorkerHandle: id => new PendingWorkerHandle(id),
            getRegisteredWork: CreateRegisteredWork);
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var handle = runtime.Start(workflow.Definition.Name, requestContext);
        await TestEventually.Until(
            () => runtime.Get(handle.RunId!.Value)?.Steps.Single().WorkerIds.Contains(childId) == true,
            "Expected the durable workflow to be waiting on its child.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Execute(handle.RunId!.Value, action, requestContext));

        Assert.Equal("control intent persistence failed", exception.Message);
        Assert.Equal(WorkflowRunStatus.Running, runtime.Get(handle.RunId!.Value)?.Status);
        Assert.Null(GetRuns(runtime)[handle.RunId.Value].GetPendingControlAction());
        Assert.Empty(workerOperations.Executions);

        runtime.CancelExecutionLifetime();
        await runtime.WaitForExecutions(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void StartExecutionThrowsWhenTheRunIsAlreadyExecuting()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.duplicate.execution"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(workerId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var startExecution = typeof(WorkflowRuntime).GetMethod(
            "StartExecution",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected StartExecution method.");

        startExecution.Invoke(runtime, [run, workflow, false]);
        var exception = Assert.Throws<TargetInvocationException>(() => startExecution.Invoke(runtime, [run, workflow, false]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("already executing", exception.InnerException!.Message, StringComparison.Ordinal);

        runtime.CancelExecutionLifetime();
    }

    [Fact]
    public async Task StartWaitsForInitialDurablePersistenceBeforeReturningAcceptedHandle()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.accepted.after.persist",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new BlockingWorkflowPersistenceStore();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(workerId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var startTask = Task.Run(() => runtime.Start(workflow.Definition.Name, requestContext));
        await store.FirstUpsertStarted.Task.WaitAsync(CancellationToken.None);

        Assert.False(startTask.IsCompleted, "Expected durable workflow start to wait for the initial persisted run.");
        Assert.Empty(runtime.ListVisible(requestContext, includeFinal: true));

        store.ReleaseFirstUpsert.TrySetResult();
        var handle = await startTask.WaitAsync(CancellationToken.None);

        Assert.True(handle.StartOutcome.IsAccepted);
        Assert.NotNull(handle.RunId);

        runtime.CancelExecutionLifetime();
        await runtime.WaitForExecutions(CancellationToken.None);
    }

    [Fact]
    public void StartDoesNotRegisterRunWhenInitialDurablePersistenceFails()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.durable.persist.failure",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new ThrowingWorkflowPersistenceStore(new InvalidOperationException("boom"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(workerId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.Start(workflow.Definition.Name, requestContext));

        Assert.Equal("boom", exception.Message);
        Assert.Empty(runtime.ListVisible(requestContext, includeFinal: true));
    }

    [Fact]
    public async Task RunExecutionReturnsFailureWhenInternalExecutionThrows()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.exception",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var runExecution = typeof(WorkflowRuntime).GetMethod(
            "RunExecution",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowRunState), typeof(RegisteredWorkflow), typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException("Expected RunExecution method.");

        var task = Assert.IsType<Task<WorkflowRunCompletion>>(
            runExecution.Invoke(runtime, [run, workflow, CancellationToken.None]));
        var completion = await task;

        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.workflow.execution_exception");
    }

    [Fact]
    public async Task RunExecutionReturnsCanceledCompletionWhenCancellationIsRequested()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.canceled"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(workerId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var runExecution = typeof(WorkflowRuntime).GetMethod(
            "RunExecution",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowRunState), typeof(RegisteredWorkflow), typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException("Expected RunExecution method.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var task = Assert.IsType<Task<WorkflowRunCompletion>>(
            runExecution.Invoke(runtime, [run, workflow, cancellation.Token]));
        var completion = await task;

        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
        Assert.Equal(WorkflowRunStatus.Canceled, run.ToSnapshot().Status);
    }

    [Fact]
    public async Task RejectControlWhenTheRegisteredWorkflowDisappearsOrTheExecutionIsOrphaned()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.control.orphaned"),
            Dispatch("dispatch", "sample.dispatch"));
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var missingDefinitionRuntime = CreateRuntime(
            catalog: new WorkflowCatalog([]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var missingDefinitionRun = WorkflowRunState.Create(workflow, requestContext);
        GetRuns(missingDefinitionRuntime).TryAdd(missingDefinitionRun.Id, missingDefinitionRun);

        var missingDefinition = await missingDefinitionRuntime.Execute(
            missingDefinitionRun.Id,
            WorkflowAction.Cancel,
            requestContext);

        var orphanedRuntime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var orphanedRun = WorkflowRunState.Create(workflow, requestContext);
        GetRuns(orphanedRuntime).TryAdd(orphanedRun.Id, orphanedRun);

        var orphaned = await orphanedRuntime.Execute(
            orphanedRun.Id,
            WorkflowAction.Cancel,
            requestContext);

        Assert.Equal(WorkflowActionStatus.Invalid, missingDefinition.Status);
        Assert.Contains(missingDefinition.Messages, message => message.Code == "workable.workflow.definition.not_found");
        Assert.Equal(WorkflowActionStatus.Invalid, orphaned.Status);
        Assert.Contains(orphaned.Messages, message => message.Code == "workable.workflow.run.not_executing");
    }

    [Fact]
    public async Task RejectResumeWhenPausedRunStillHasExecutionBookkeeping()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.resume.executing"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var run = WorkflowRunState.Create(workflow, requestContext);
        run.Pause();
        GetRuns(runtime).TryAdd(run.Id, run);
        GetExecutions(runtime).TryAdd(
            run.Id,
            Task.FromResult(new WorkflowRunCompletion(WorkflowRunStatus.Paused, run.ToSnapshot(), [])));

        var outcome = await runtime.Execute(run.Id, WorkflowAction.Start, requestContext);

        Assert.Equal(WorkflowActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message => message.Code == "workable.workflow.run.executing");
    }

    [Fact]
    public async Task KeepPausedRunUnchangedWhenManualResumePersistenceFails()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.resume-persistence-failure",
                coordination: WorkflowCoordinationConfiguration.Durable));
        var store = new RawWorkflowPersistenceStore(
            [],
            persisted => persisted.Status == WorkflowRunStatus.Running
                ? Task.FromException(new InvalidOperationException("resume persistence failed"))
                : Task.CompletedTask);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("A failed resume must not execute."))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var run = WorkflowRunState.Create(workflow, requestContext);
        run.Pause();
        GetRuns(runtime).TryAdd(run.Id, run);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Execute(run.Id, WorkflowAction.Start, requestContext));

        Assert.Equal("resume persistence failed", exception.Message);
        Assert.Equal(WorkflowRunStatus.Paused, run.GetStatus());
        Assert.Empty(GetExecutions(runtime));
        Assert.Equal(0, GetActionGateCount(runtime));
    }

    [Fact]
    public async Task PersistDurableCancellationAppliedDirectlyToAPausedRun()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.paused.cancel",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("No child should be queued while canceling a paused run."))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var run = WorkflowRunState.Create(workflow, requestContext);
        run.Pause();
        GetRuns(runtime).TryAdd(run.Id, run);

        var outcome = await runtime.Execute(run.Id, WorkflowAction.Cancel, requestContext);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Canceled, run.ToSnapshot().Status);
        Assert.Contains(store.UpsertedRuns, persisted =>
            persisted.RunId == run.Id && persisted.Status == WorkflowRunStatus.Canceled);
    }

    [Fact]
    public async Task KeepBlockedWorkflowNonFinalWhenChildCancellationIsRejected()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.rejected-child-cancel",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var workers = new RejectingWorkerOperations();
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new InvalidOperationException("No dispatch expected.")),
                workers,
                new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(CreateSnapshot(workerId, WorkerState.Running)))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var run = WorkflowRunState.Create(workflow, requestContext);
        run.MarkStepCompleted("dispatch", [workerId]);
        run.Block([WorkMessage.Error("sample.blocked", "Blocked before cancellation.")]);
        GetRuns(runtime).TryAdd(run.Id, run);

        var outcome = await runtime.Execute(run.Id, WorkflowAction.Cancel, requestContext);

        Assert.False(outcome.IsAccepted);
        Assert.Equal(WorkflowActionStatus.Invalid, outcome.Status);
        Assert.Equal(WorkflowRunStatus.Blocked, run.GetStatus());
        Assert.Contains(outcome.Messages, message => message.Code == "workable.worker.unauthorized");
        Assert.Single(workers.Executions);
    }

    [Theory]
    [InlineData("Cancel", WorkflowRunStatus.Canceled)]
    [InlineData("Pause", WorkflowRunStatus.Paused)]
    public async Task SettleDurableExecutionWhenControlIsRequestedBeforeTheExecutorObservesIt(
        string actionName,
        WorkflowRunStatus expectedStatus)
    {
        var action = Enum.Parse<WorkflowAction>(actionName);
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                $"workflow.runtime.preemptive.{action.ToString().ToLowerInvariant()}",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("A preemptively canceled execution must not queue work."))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var controlType = typeof(WorkflowRuntime).GetNestedType(
            "WorkflowExecutionControl",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected workflow execution control type.");
        var control = Activator.CreateInstance(
            controlType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [CancellationToken.None],
            culture: null)
            ?? throw new InvalidOperationException("Expected workflow execution control instance.");
        controlType.GetMethod(action == WorkflowAction.Cancel ? "RequestCancel" : "RequestPause")!
            .Invoke(control, null);
        var runExecution = typeof(WorkflowRuntime).GetMethod(
            "RunExecution",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowRunState), typeof(RegisteredWorkflow), controlType, typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Expected controlled RunExecution method.");

        try
        {
            var task = Assert.IsType<Task<WorkflowRunCompletion>>(
                runExecution.Invoke(runtime, [run, workflow, control, false]));
            var completion = await task;

            Assert.Equal(expectedStatus, completion.Status);
            Assert.Contains(store.UpsertedRuns, persisted =>
                persisted.RunId == run.Id && persisted.Status == expectedStatus);
        }
        finally
        {
            ((IDisposable)control).Dispose();
        }
    }

    [Fact]
    public async Task BlockCanceledExecutionWhenOutstandingChildRejectsCancellation()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.active-rejected-child-cancel",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var workers = new RejectingWorkerOperations();
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new InvalidOperationException("No dispatch expected.")),
                workers,
                new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(CreateSnapshot(workerId, WorkerState.Running)))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [workerId]);
        var controlType = typeof(WorkflowRuntime).GetNestedType(
            "WorkflowExecutionControl",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected workflow execution control type.");
        var control = Activator.CreateInstance(
            controlType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [CancellationToken.None],
            culture: null)
            ?? throw new InvalidOperationException("Expected workflow execution control instance.");
        controlType.GetMethod("RequestCancel")!.Invoke(control, null);
        var runExecution = typeof(WorkflowRuntime).GetMethod(
            "RunExecution",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowRunState), typeof(RegisteredWorkflow), controlType, typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Expected controlled RunExecution method.");

        try
        {
            var task = Assert.IsType<Task<WorkflowRunCompletion>>(
                runExecution.Invoke(runtime, [run, workflow, control, false]));
            var completion = await task;

            Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
            Assert.False(completion.IsFinal);
            Assert.Contains(completion.Messages, message => message.Code == "workable.worker.unauthorized");
            Assert.Contains(store.UpsertedRuns, persisted =>
                persisted.RunId == run.Id && persisted.Status == WorkflowRunStatus.Blocked);
        }
        finally
        {
            ((IDisposable)control).Dispose();
        }
    }

    [Fact]
    public async Task BlockNonDurableCanceledExecutionWhenOutstandingChildRejectsCancellation()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.non-durable-rejected-child-cancel"),
            Dispatch("dispatch", "sample.dispatch"));
        var workers = new RejectingWorkerOperations();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new InvalidOperationException("No dispatch expected.")),
                workers,
                new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(CreateSnapshot(workerId, WorkerState.Running)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [workerId]);
        var controlType = typeof(WorkflowRuntime).GetNestedType(
            "WorkflowExecutionControl",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected workflow execution control type.");
        var control = Activator.CreateInstance(
            controlType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [CancellationToken.None],
            culture: null)
            ?? throw new InvalidOperationException("Expected workflow execution control instance.");
        controlType.GetMethod("RequestCancel")!.Invoke(control, null);
        var runExecution = typeof(WorkflowRuntime).GetMethod(
            "RunExecution",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowRunState), typeof(RegisteredWorkflow), controlType, typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Expected controlled RunExecution method.");

        try
        {
            var task = Assert.IsType<Task<WorkflowRunCompletion>>(
                runExecution.Invoke(runtime, [run, workflow, control, false]));
            var completion = await task;

            Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
            Assert.False(completion.IsFinal);
            Assert.Equal(WorkflowRunStatus.Blocked, run.GetStatus());
            Assert.Contains(completion.Messages, message => message.Code == "workable.worker.unauthorized");
            Assert.Single(workers.Executions);
        }
        finally
        {
            ((IDisposable)control).Dispose();
        }
    }

    [Fact]
    public async Task BlockNonDurableCancelWorkflowPolicyWhenSiblingCancellationIsRejected()
    {
        var canceledWorkerId = WorkerId.New();
        var runningWorkerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.non-durable-policy-rejected-child-cancel"),
            new DispatchEachWorkflowStepDefinition(
                "fan-out",
                new WorkflowStepReference<object?>("load"),
                WorkDefinition.Create("sample.process"),
                new WorkflowOutputSelector("/items"),
                WorkflowCanceledChildBehavior.CancelWorkflow));
        var workers = new RejectingWorkerOperations();
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new InvalidOperationException("No dispatch expected.")),
                workers,
                new DelegateQueryService(workerId => Task.FromResult<WorkerSnapshot?>(
                    CreateSnapshot(
                        workerId,
                        workerId == canceledWorkerId ? WorkerState.Canceled : WorkerState.Running)))),
            createWorkerHandle: workerId => workerId == canceledWorkerId
                ? new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(WorkCompletionStatus.Canceled, null, null, [])))
                : new PendingWorkerHandle(workerId));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("fan-out", [canceledWorkerId, runningWorkerId]);
        var controlType = typeof(WorkflowRuntime).GetNestedType(
            "WorkflowExecutionControl",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected workflow execution control type.");
        var control = Activator.CreateInstance(
            controlType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [CancellationToken.None],
            culture: null)
            ?? throw new InvalidOperationException("Expected workflow execution control instance.");
        var runExecution = typeof(WorkflowRuntime).GetMethod(
            "RunExecution",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowRunState), typeof(RegisteredWorkflow), controlType, typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Expected controlled RunExecution method.");

        try
        {
            var task = Assert.IsType<Task<WorkflowRunCompletion>>(
                runExecution.Invoke(runtime, [run, workflow, control, false]));
            var completion = await task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(WorkflowRunStatus.Blocked, completion.Status);
            Assert.False(completion.IsFinal);
            Assert.Equal(WorkflowRunStatus.Blocked, run.GetStatus());
            Assert.Contains(completion.Messages, message => message.Code == "workable.worker.unauthorized");
            Assert.Equal([(runningWorkerId, WorkAction.Cancel)], workers.Executions);
        }
        finally
        {
            ((IDisposable)control).Dispose();
        }
    }

    [Fact]
    public async Task BlockCanceledChildPolicyWhenSiblingCancellationIsRejected()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.policy-rejected-child-cancel",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => throw new InvalidOperationException("No dispatch expected.")),
                new RejectingWorkerOperations(),
                new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(
                    CreateSnapshot(workerId, WorkerState.Running)))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", [workerId]);
        run.Pause();
        GetRuns(runtime).TryAdd(run.Id, run);
        var applyCancellation = typeof(WorkflowRuntime).GetMethod(
            "ApplyCanceledChildWorkflowCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected canceled-child policy handler.");

        var task = Assert.IsAssignableFrom<Task>(applyCancellation.Invoke(
            runtime,
            [run, workflow, CancellationToken.None]));
        await task;

        Assert.Equal(WorkflowRunStatus.Blocked, run.GetStatus());
        Assert.Contains(run.ToSnapshot().Messages, message => message.Code == "workable.worker.unauthorized");
        Assert.Contains(store.UpsertedRuns, persisted => persisted.Status == WorkflowRunStatus.Blocked);
        Assert.Equal(0, GetActionGateCount(runtime));
    }

    [Fact]
    public async Task FinalRunPurgeIgnoresANonFinalRun()
    {
        var workflow = CreateWorkflow(WorkflowDefinition.Create("workflow.runtime.non-final-purge"));
        var store = new RawWorkflowPersistenceStore([]);
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var tryPurge = typeof(WorkflowRuntime).GetMethod(
            "TryPurgeFinalRunIfChildrenGone",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected final workflow purge method.");

        var task = Assert.IsAssignableFrom<Task>(tryPurge.Invoke(
            runtime,
            [run, CancellationToken.None, null]));
        await task;

        Assert.Empty(store.DeletedRuns);
        Assert.Equal(WorkflowRunStatus.Running, run.GetStatus());
    }

    [Fact]
    public async Task WaitForExecutionsReturnsAfterCancelingTheExecutionLifetime()
    {
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.wait"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new PendingWorkerHandle(workerId)))),
            createWorkerHandle: id => new PendingWorkerHandle(id));
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var startExecution = typeof(WorkflowRuntime).GetMethod(
            "StartExecution",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected StartExecution method.");

        startExecution.Invoke(runtime, [run, workflow, false]);
        runtime.CancelExecutionLifetime();
        await runtime.WaitForExecutions(CancellationToken.None);
        var completion = await run.WaitForCompletion();

        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task WaitForExecutionsWaitsForRunningExecutionsToFinish()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerId = WorkerId.New();
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.wait.complete"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                Task.FromResult<IWorkerHandle>(new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    release.Task.ContinueWith(
                        _ => new WorkCompletion(WorkCompletionStatus.Completed, null, null, []),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default))))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var startExecution = typeof(WorkflowRuntime).GetMethod(
            "StartExecution",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected StartExecution method.");

        startExecution.Invoke(runtime, [run, workflow, false]);
        var waitTask = runtime.WaitForExecutions(CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        release.TrySetResult();
        await waitTask;

        var completion = await run.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void StartExecutionRejectsDuplicateExecutionBookkeeping()
    {
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create("workflow.runtime.duplicate-execution"),
            Dispatch("dispatch", "sample.dispatch"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var run = WorkflowRunState.Create(workflow, WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        GetExecutions(runtime).TryAdd(
            run.Id,
            Task.FromResult(new WorkflowRunCompletion(WorkflowRunStatus.Running, run.ToSnapshot(), [])));
        var startExecution = typeof(WorkflowRuntime).GetMethod(
            "StartExecution",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected StartExecution method.");

        var exception = Assert.Throws<TargetInvocationException>(() =>
            startExecution.Invoke(runtime, [run, workflow, false]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task ImmediateExecutionsLeaveNoStaleBookkeeping()
    {
        var workflow = CreateWorkflow(WorkflowDefinition.Create("workflow.runtime.immediate"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("An empty workflow must not dispatch work."))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var handles = Enumerable.Range(0, 128)
            .Select(_ => runtime.Start(workflow.Definition.Name, requestContext))
            .ToArray();
        await Task.WhenAll(handles.Select(handle => handle.WaitForCompletion()));
        await runtime.WaitForExecutions(CancellationToken.None);

        Assert.Empty(GetExecutions(runtime));
    }

    [Fact]
    public async Task ActionGatesAreReleasedForMissingRuns()
    {
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => throw new NotSupportedException(),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 128).Select(_ =>
            runtime.Execute(WorkflowRunId.New(), WorkflowAction.Cancel, requestContext)));

        Assert.All(outcomes, outcome => Assert.Equal(WorkflowActionStatus.NotFound, outcome.Status));
        Assert.Equal(0, GetActionGateCount(runtime));
    }

    [Fact]
    public async Task ResumeWaitsForAFaultedPriorExecutionToLeaveBookkeeping()
    {
        var workflow = CreateWorkflow(WorkflowDefinition.Create("workflow.runtime.resume-after-fault"));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: null,
            systemName: null,
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("An empty workflow must not dispatch work."))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var run = WorkflowRunState.Create(workflow, requestContext);
        run.Pause();
        GetRuns(runtime).TryAdd(run.Id, run);
        var priorExecution = new TaskCompletionSource<WorkflowRunCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        GetExecutions(runtime).TryAdd(run.Id, priorExecution.Task);

        var resume = runtime.Execute(run.Id, WorkflowAction.Start, requestContext);
        Assert.False(resume.IsCompleted);
        GetExecutions(runtime).TryRemove(run.Id, out _);
        priorExecution.TrySetException(new InvalidOperationException("Prior execution failed after pausing."));

        var outcome = await resume.WaitAsync(TimeSpan.FromSeconds(1));
        var completion = await run.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(outcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(0, GetActionGateCount(runtime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredExecutionCleansUpWhenFinalPersistenceDoesNotComplete(bool isCanceled)
    {
        var failedWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception persistenceFailure = isCanceled
            ? new OperationCanceledException("Persistence canceled.", new CancellationToken(canceled: true))
            : new InvalidOperationException("Persistence failed.");
        var store = new RawWorkflowPersistenceStore(
            [],
            async run =>
            {
                if (run.Status == WorkflowRunStatus.Failed)
                {
                    failedWriteEntered.TrySetResult();
                    await releaseFailedWrite.Task;
                    throw persistenceFailure;
                }
            });
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                $"workflow.runtime.persistence-{(isCanceled ? "canceled" : "failed")}",
                coordination: WorkflowCoordinationConfiguration.Durable));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => throw new InvalidOperationException("Expected execution failure."),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var handle = runtime.Start(workflow.Definition.Name, requestContext);
        await failedWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var execution = GetExecutions(runtime)[handle.RunId!.Value];
        var publicCompletion = handle.WaitForCompletion();

        Assert.False(publicCompletion.IsCompleted);
        Assert.Equal(WorkflowRunStatus.Running, runtime.Get(handle.RunId.Value)?.Status);
        releaseFailedWrite.TrySetResult();

        if (isCanceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publicCompletion);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
            var publicException = await Assert.ThrowsAsync<InvalidOperationException>(() => publicCompletion);
            Assert.Equal("Persistence failed.", exception.Message);
            Assert.Equal("Persistence failed.", publicException.Message);
        }

        Assert.Empty(GetExecutions(runtime));
        Assert.Equal(0, GetActionGateCount(runtime));
        Assert.Equal(WorkflowRunStatus.Running, runtime.Get(handle.RunId.Value)?.Status);
        Assert.Null(runtime.Get(handle.RunId.Value)?.CompletedAt);
    }

    [Fact]
    public async Task SuccessfulDurableCompletionFaultsWhenItsFinalPersistenceFails()
    {
        var finalWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RawWorkflowPersistenceStore(
            [],
            async run =>
            {
                if (run.Status == WorkflowRunStatus.Completed)
                {
                    finalWriteEntered.TrySetResult();
                    await releaseFinalWrite.Task;
                    throw new InvalidOperationException("Final workflow persistence failed.");
                }
            });
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.successful-final-persistence-failure",
                coordination: WorkflowCoordinationConfiguration.Durable));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("An empty workflow must not dispatch work."))),
            createWorkerHandle: _ => throw new NotSupportedException());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var handle = runtime.Start(workflow.Definition.Name, requestContext);
        await finalWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var execution = GetExecutions(runtime)[handle.RunId!.Value];
        var publicCompletion = handle.WaitForCompletion();
        Assert.False(publicCompletion.IsCompleted);
        Assert.Equal(WorkflowRunStatus.Running, runtime.Get(handle.RunId.Value)?.Status);
        Assert.Null(runtime.Get(handle.RunId.Value)?.CompletedAt);
        releaseFinalWrite.TrySetResult();

        var publicFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => publicCompletion);
        var executionFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);

        Assert.Equal("Final workflow persistence failed.", publicFailure.Message);
        Assert.Equal("Final workflow persistence failed.", executionFailure.Message);
        Assert.Empty(GetExecutions(runtime));
        Assert.Equal(WorkflowRunStatus.Running, runtime.Get(handle.RunId.Value)?.Status);
        Assert.Null(runtime.Get(handle.RunId.Value)?.CompletedAt);

        var persistedRunning = Assert.Single(
            store.UpsertedRuns,
            run => run.Status == WorkflowRunStatus.Running);
        var recoveredFinalWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecoveredFinalWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryStore = new RawWorkflowPersistenceStore(
            [persistedRunning],
            async run =>
            {
                if (run.Status == WorkflowRunStatus.Completed)
                {
                    recoveredFinalWriteEntered.TrySetResult();
                    await releaseRecoveredFinalWrite.Task;
                }
            });
        var recoveredRuntime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: recoveryStore,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(new DelegateQueueService((_, _, _, _) =>
                throw new InvalidOperationException("An empty workflow must not dispatch work."))),
            createWorkerHandle: _ => throw new NotSupportedException());

        await recoveredRuntime.RecoverDurableRuns(CancellationToken.None);
        await recoveredFinalWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkflowRunStatus.Running, recoveredRuntime.Get(handle.RunId.Value)?.Status);
        releaseRecoveredFinalWrite.TrySetResult();
        await recoveredRuntime.WaitForExecutions(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(recoveredRuntime.Get(handle.RunId.Value));
        Assert.Contains(
            recoveryStore.UpsertedRuns,
            run => run.Status == WorkflowRunStatus.Completed && run.RunId == handle.RunId.Value);
        Assert.Contains(handle.RunId.Value, recoveryStore.DeletedRuns);
    }

    [Fact]
    public async Task DoNotReturnAStaleBlockedCompletionAfterManualCancellation()
    {
        var workerId = WorkerId.New();
        var blockedWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RawWorkflowPersistenceStore(
            [],
            async run =>
            {
                if (run.Status == WorkflowRunStatus.Blocked)
                {
                    blockedWriteEntered.TrySetResult();
                    await releaseBlockedWrite.Task;
                }
            });
        var workflow = CreateWorkflow(
            WorkflowDefinition.Create(
                "workflow.runtime.blocked-cancel-race",
                coordination: WorkflowCoordinationConfiguration.Durable),
            Dispatch("dispatch", "sample.dispatch"));
        var failedCompletion = Task.FromResult(new WorkCompletion(
            WorkCompletionStatus.Failed,
            null,
            null,
            [WorkMessage.Error("sample.failed", "Expected child failure.")]));
        var runtime = CreateRuntime(
            catalog: new WorkflowCatalog([workflow]),
            persistenceStore: store,
            systemName: "workflow-tests",
            createSession: _ => new TestWorkSystemSession(
                new DelegateQueueService((_, _, _, _) => Task.FromResult<IWorkerHandle>(
                    new TestWorkerHandle(WorkQueueOutcome.Accepted(workerId), workerId, failedCompletion))),
                query: new DelegateQueryService(id => Task.FromResult<WorkerSnapshot?>(
                    id == workerId ? CreateSnapshot(workerId, WorkerState.Failed) : null))),
            createWorkerHandle: _ => new TestWorkerHandle(
                WorkQueueOutcome.Accepted(workerId),
                workerId,
                failedCompletion),
            getRegisteredWork: CreateRegisteredWork);
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var handle = runtime.Start(workflow.Definition.Name, requestContext);
        await blockedWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var execution = GetExecutions(runtime)[handle.RunId!.Value];
        var cancellation = runtime.Execute(handle.RunId.Value, WorkflowAction.Cancel, requestContext);
        releaseBlockedWrite.TrySetResult();

        var action = await cancellation.WaitAsync(TimeSpan.FromSeconds(1));
        var executionCompletion = await execution.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(action.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Canceled, executionCompletion.Status);
        Assert.Equal(WorkflowRunStatus.Canceled, runtime.Get(handle.RunId.Value)?.Status);
    }

    private static WorkflowRuntime CreateRuntime(
        WorkflowCatalog catalog,
        IWorkPersistenceStore? persistenceStore,
        string? systemName,
        Func<WorkRequestContext, IWorkSystemSession> createSession,
        Func<WorkerId, IWorkerHandle> createWorkerHandle,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker = null,
        Func<string, RegisteredWork?>? getRegisteredWork = null)
        => new(
            systemName,
            requiresAuthorization: false,
            catalog,
            getRegisteredWork ?? (_ => null),
            createSession,
            createWorkerHandle,
            getAuthoritativeWorker,
            new WorkflowPersistenceCoordinator(persistenceStore, systemName),
            WorkSystemAuthorizationConfiguration.Default,
            new EmptyGroupProvider());

    private static RegisteredWork CreateRegisteredWork(string name)
        => new(
            WorkDefinition.Create(name),
            _ => throw new NotSupportedException(),
            []);

    private static ConcurrentDictionary<WorkflowRunId, WorkflowRunState> GetRuns(WorkflowRuntime runtime)
        => (ConcurrentDictionary<WorkflowRunId, WorkflowRunState>)(typeof(WorkflowRuntime).GetField(
            "runs",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(runtime)
            ?? throw new InvalidOperationException("Expected workflow run registry."));

    private static ConcurrentDictionary<WorkflowRunId, Task<WorkflowRunCompletion>> GetExecutions(
        WorkflowRuntime runtime)
        => (ConcurrentDictionary<WorkflowRunId, Task<WorkflowRunCompletion>>)(typeof(WorkflowRuntime).GetField(
            "executions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(runtime)
            ?? throw new InvalidOperationException("Expected workflow execution registry."));

    private static int GetActionGateCount(WorkflowRuntime runtime)
    {
        var gates = typeof(WorkflowRuntime).GetField(
            "actionGates",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(runtime)
            ?? throw new InvalidOperationException("Expected workflow action gate registry.");
        return (int)(gates.GetType().GetProperty("Count")?.GetValue(gates)
            ?? throw new InvalidOperationException("Expected workflow action gate count."));
    }

    private static RegisteredWorkflow CreateWorkflow(
        WorkflowDefinition definition,
        params WorkflowStepDefinition[] steps)
        => new(
            definition,
            steps,
            WorkOperateAuthorizationConfiguration.None);

    private static WorkflowRunPersistenceRecord CreatePersistedRun(
        string systemName,
        WorkflowRunId runId,
        WorkflowDefinition definition,
        WorkerId workerId,
        string? pendingControlAction = null,
        WorkRequestContext? pendingControlRequestContext = null)
    {
        var workflow = CreateWorkflow(
            definition,
            Dispatch("dispatch", "sample.dispatch"),
            new JoinWorkflowStepDefinition("join"));
        return new WorkflowRunPersistenceRecord(
            systemName,
            runId,
            definition.Version,
            definition.Name,
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
                    [],
                    DateTimeOffset.UtcNow,
                    null,
                    []),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            [],
            WorkflowDefinitionFingerprint.Create(workflow),
            pendingControlAction,
            pendingControlRequestContext);
    }

    private static WorkerSnapshot CreateSnapshot(
        WorkerId workerId,
        WorkerState state,
        IReadOnlySet<WorkIdentifier>? identifiers = null)
        => new(
            workerId,
            Revision: 1,
            StateSequence: 1,
            DefinitionName: "sample.dispatch",
            DefinitionCategory: string.Empty,
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: identifiers ?? new HashSet<WorkIdentifier>(),
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

    private sealed class EmptyGroupProvider : IWorkAuthorizationGroupProvider
    {
        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

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

        public IWorkerOperations Workers { get; } = workers ?? new RecordingWorkerOperations();

        public IWorkQueryService Query { get; } = query ?? new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null));

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();
    }

    private sealed class DelegateQueueService(
        Func<string, WorkInput?, WorkerOptions?, CancellationToken, Task<IWorkerHandle>> enqueue)
        : IWorkQueueService
    {
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

    private sealed class RecordingWorkerOperations : IWorkerOperations
    {
        public List<(WorkerId WorkerId, WorkAction Action)> Executions { get; } = [];

        public Task<WorkActionOutcome> Execute(WorkerVersion worker, WorkAction action, CancellationToken cancellationToken = default)
        {
            this.Executions.Add((worker.WorkerId, action));
            return Task.FromResult(WorkActionOutcome.Accepted(action, CreateSnapshot(worker.WorkerId, WorkerState.Canceled), []));
        }

        public Task<WorkActionOutcome> Execute(WorkerVersion worker, WorkerActionRequest request, CancellationToken cancellationToken = default)
            => this.Execute(worker, request.Action, cancellationToken);

        public Task<WorkerBulkActionOutcome> ExecuteAll(WorkAction action, WorkerBulkActionFilter? filter = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkActionOutcome> Reconfigure(WorkerVersion worker, WorkerReconfiguration changes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RejectingWorkerOperations : IWorkerOperations
    {
        public List<(WorkerId WorkerId, WorkAction Action)> Executions { get; } = [];

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkAction action,
            CancellationToken cancellationToken = default)
        {
            this.Executions.Add((worker.WorkerId, action));
            return Task.FromResult(WorkActionOutcome.Unauthorized(action, worker.WorkerId));
        }

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkerActionRequest request,
            CancellationToken cancellationToken = default)
            => this.Execute(worker, request.Action, cancellationToken);

        public Task<WorkerBulkActionOutcome> ExecuteAll(
            WorkAction action,
            WorkerBulkActionFilter? filter = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkActionOutcome> Reconfigure(
            WorkerVersion worker,
            WorkerReconfiguration changes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class BlockingRecordingWorkerOperations : IWorkerOperations
    {
        public List<(WorkerId WorkerId, WorkAction Action)> Executions { get; } = [];

        public TaskCompletionSource ExecutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkAction action,
            CancellationToken cancellationToken = default)
            => this.Execute(worker, new WorkerActionRequest(action), cancellationToken);

        public async Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkerActionRequest request,
            CancellationToken cancellationToken = default)
        {
            this.ExecutionStarted.TrySetResult();
            await this.ReleaseExecution.Task.WaitAsync(cancellationToken);
            this.Executions.Add((worker.WorkerId, request.Action));
            return WorkActionOutcome.Accepted(
                request.Action,
                CreateSnapshot(worker.WorkerId, WorkerState.Canceled),
                []);
        }

        public Task<WorkerBulkActionOutcome> ExecuteAll(
            WorkAction action,
            WorkerBulkActionFilter? filter = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkActionOutcome> Reconfigure(
            WorkerVersion worker,
            WorkerReconfiguration changes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class PendingWorkerHandle(WorkerId workerId) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = WorkQueueOutcome.Accepted(workerId);

        public WorkerId? WorkerId { get; } = workerId;

        public async Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new WorkCompletion(WorkCompletionStatus.Completed, null, null, []);
        }

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
    }

    private sealed class TestWorkerHandle(
        WorkQueueOutcome queueOutcome,
        WorkerId? workerId,
        Task<WorkCompletion> completion) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId { get; } = workerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => completion;

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
    }

    private sealed class RawWorkflowPersistenceStore(
        IReadOnlyList<WorkflowRunPersistenceRecord> runs,
        Func<WorkflowRunPersistenceRecord, Task>? upsertHandler = null) : IWorkPersistenceStore
    {
        public int ListCalls { get; private set; }

        public List<WorkflowRunId> DeletedRuns { get; } = [];

        public List<WorkflowRunPersistenceRecord> UpsertedRuns { get; } = [];

        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            => Task.CompletedTask;

        public Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
            WorkflowPersistenceTransactionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IWorkflowPersistenceTransaction>(new RawWorkflowTransaction());

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.ListCalls++;
            foreach (var run in runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return run;
            }

            await Task.CompletedTask;
        }

        public async Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
        {
            this.UpsertedRuns.Add(run);
            if (upsertHandler is not null)
            {
                await upsertHandler(run);
            }
        }

        public async Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            this.UpsertedRuns.Add(run);
            if (upsertHandler is not null)
            {
                await upsertHandler(run);
            }
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            this.DeletedRuns.Add(request.RunId);
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            this.DeletedRuns.Add(request.RunId);
            return Task.CompletedTask;
        }

        private sealed class RawWorkflowTransaction : IWorkflowPersistenceTransaction
        {
            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

            public Task Commit(CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }

    private sealed class BlockingWorkflowPersistenceStore : IWorkPersistenceStore
    {
        public TaskCompletionSource FirstUpsertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstUpsert { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            => Task.CompletedTask;

        public Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
            WorkflowPersistenceTransactionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IWorkflowPersistenceTransaction>(new NoopWorkflowPersistenceTransaction());

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
        {
            this.FirstUpsertStarted.TrySetResult();
            await this.ReleaseFirstUpsert.Task.WaitAsync(cancellationToken);
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> DurableWorkerExists(WorkerId workerId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class ThrowingWorkflowPersistenceStore(Exception exception) : IWorkPersistenceStore
    {
        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            => Task.CompletedTask;

        public Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
            WorkflowPersistenceTransactionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IWorkflowPersistenceTransaction>(new NoopWorkflowPersistenceTransaction());

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> DurableWorkerExists(WorkerId workerId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NoopWorkflowPersistenceTransaction : IWorkflowPersistenceTransaction
    {
        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        public Task Commit(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static DispatchWorkflowStepDefinition Dispatch(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null)
        => new(stepName, WorkDefinition.Create(workDefinitionName), input);
}

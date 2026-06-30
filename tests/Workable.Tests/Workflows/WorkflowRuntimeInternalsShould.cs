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
        var workerOperations = new RecordingWorkerOperations();
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

        await TestEventually.Until(
            () => runtime.Get(persistedRun.RunId)?.Status == WorkflowRunStatus.Canceled,
            "Expected the recovered durable workflow run to honor the persisted cancel request.",
            timeout: TimeSpan.FromSeconds(10));

        Assert.Contains(workerOperations.Executions, execution =>
            execution.WorkerId == workerId && execution.Action == WorkAction.Cancel);
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

    private static WorkflowRuntime CreateRuntime(
        WorkflowCatalog catalog,
        IWorkPersistenceStore? persistenceStore,
        string? systemName,
        Func<WorkRequestContext, IWorkSystemSession> createSession,
        Func<WorkerId, IWorkerHandle> createWorkerHandle)
        => new(
            systemName,
            requiresAuthorization: false,
            catalog,
            _ => null,
            createSession,
            createWorkerHandle,
            null,
            new WorkflowPersistenceCoordinator(persistenceStore, systemName),
            WorkSystemAuthorizationConfiguration.Default,
            new EmptyGroupProvider());

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
        string? pendingControlAction = null)
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
            pendingControlAction);
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

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue { get; } = queue;

        public IWorkerOperations Workers { get; } = workers ?? new RecordingWorkerOperations();

        public IWorkQueryService Query { get; } = query ?? new DelegateQueryService(_ => Task.FromResult<WorkerSnapshot?>(null));

        public IWorkEventStream Events => throw new NotSupportedException();
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

        public Task<WorkerBulkActionOutcome> ExecuteAll(WorkAction action, WorkerBulkActionFilter? filter = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkActionOutcome> Reconfigure(WorkerVersion worker, WorkerReconfiguration changes, CancellationToken cancellationToken = default)
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

    private sealed class RawWorkflowPersistenceStore(IReadOnlyList<WorkflowRunPersistenceRecord> runs) : IWorkPersistenceStore
    {
        public int ListCalls { get; private set; }

        public List<WorkflowRunId> DeletedRuns { get; } = [];

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

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

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

    private static DispatchWorkflowStepDefinition Dispatch(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null)
        => new(stepName, WorkDefinition.Create(workDefinitionName), input);
}

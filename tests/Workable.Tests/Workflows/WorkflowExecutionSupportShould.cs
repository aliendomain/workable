using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowExecutionSupportShould
{
    [Fact]
    public async Task ReturnCompletedWhenNoOutstandingWorkersExist()
    {
        var completion = await WorkflowExecutionSupport.WaitForOutstanding([], CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(WorkflowRunStatus.Completed, completion.Status);
    }

    [Fact]
    public async Task ReturnCompletedWithoutCreatingHandlesWhenNoOutstandingWorkersExist()
    {
        var createdHandles = 0;

        var completion = await WorkflowExecutionSupport.WaitForOutstanding(
            [],
            _ =>
            {
                Interlocked.Increment(ref createdHandles);
                throw new InvalidOperationException("No handles should be created.");
            },
            CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(0, Volatile.Read(ref createdHandles));
    }

    [Fact]
    public async Task DistinctWorkerIdsBeforeWaitingForCompletion()
    {
        var workerId = WorkerId.New();
        var createdHandles = 0;

        var completion = await WorkflowExecutionSupport.WaitForOutstanding(
            [workerId, workerId],
            _ =>
            {
                Interlocked.Increment(ref createdHandles);
                return new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(WorkCompletionStatus.Completed, null, null, [])));
            },
            CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref createdHandles));
    }

    [Theory]
    [InlineData(WorkCompletionStatus.Completed, WorkflowRunStatus.Completed)]
    [InlineData(WorkCompletionStatus.Canceled, WorkflowRunStatus.Blocked)]
    [InlineData(WorkCompletionStatus.Failed, WorkflowRunStatus.Blocked)]
    [InlineData(WorkCompletionStatus.Interrupted, WorkflowRunStatus.Blocked)]
    [InlineData(WorkCompletionStatus.NotFound, WorkflowRunStatus.Failed)]
    [InlineData(WorkCompletionStatus.Invalid, WorkflowRunStatus.Failed)]
    [InlineData(WorkCompletionStatus.Executing, WorkflowRunStatus.Failed)]
    [InlineData(WorkCompletionStatus.Paused, WorkflowRunStatus.Blocked)]
    public void MapWorkerCompletionStatusesToWorkflowStatuses(
        WorkCompletionStatus status,
        WorkflowRunStatus expected)
    {
        Assert.Equal(expected, WorkflowExecutionSupport.ToWorkflowStatus(status));
    }

    [Fact]
    public void AddWorkflowIdentifiersPreservesExistingInputMetadata()
    {
        var input = WorkInput.Empty
            .WithSubject(new WorkSubjectId("order", "42"))
            .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "acme"))
            .WithIdentifier(new WorkIdentifier("existing", "value"));

        var updated = WorkflowExecutionSupport.AddWorkflowIdentifiers(
            input,
            new WorkflowRunId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            "workflow.demo",
            "dispatch");

        Assert.Equal(input.SubjectId, updated.SubjectId);
        Assert.Equal(input.ConcurrencyKey, updated.ConcurrencyKey);
        Assert.Contains(new WorkIdentifier("existing", "value"), updated.Identifiers!);
        Assert.Contains(new WorkIdentifier("workflow-definition", "workflow.demo"), updated.Identifiers!);
        Assert.Contains(new WorkIdentifier("workflow-step", "dispatch"), updated.Identifiers!);
        Assert.Contains(
            updated.Identifiers!,
                identifier => identifier.Type == "workflow-run" &&
                identifier.Value == "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    }

    [Fact]
    public async Task CancelOutstandingChildrenCancelsDistinctNonFinalWorkersOnly()
    {
        var runningWorkerId = WorkerId.New();
        var completedWorkerId = WorkerId.New();
        var missingWorkerId = WorkerId.New();
        var run = CreateRunWithOutstandingWorkers(
            runningWorkerId,
            runningWorkerId,
            completedWorkerId,
            missingWorkerId);
        var workers = new RecordingWorkerOperations();
        var session = new TestWorkSystemSession(workers);
        var snapshots = CreateSnapshotSource(new Dictionary<WorkerId, Queue<WorkerSnapshot?>>
        {
            [runningWorkerId] = new([CreateSnapshot(runningWorkerId, WorkerState.Running)]),
            [completedWorkerId] = new([CreateSnapshot(completedWorkerId, WorkerState.Completed)]),
            [missingWorkerId] = new([null]),
        });

        await WorkflowExecutionSupport.CancelOutstandingChildren(run, session, snapshots, CancellationToken.None);

        Assert.Collection(
            workers.Executions,
            execution =>
            {
                Assert.Equal(runningWorkerId, execution.Worker.WorkerId);
                Assert.Equal(WorkAction.Cancel, execution.Action);
            });
    }

    [Fact]
    public async Task PauseOutstandingChildrenPausesOnlyPausableOutstandingWorkers()
    {
        var transitioningWorkerId = WorkerId.New();
        var pausedWorkerId = WorkerId.New();
        var completedWorkerId = WorkerId.New();
        var failedWorkerId = WorkerId.New();
        var queuedWorkerId = WorkerId.New();
        var run = CreateRunWithOutstandingWorkers(
            transitioningWorkerId,
            pausedWorkerId,
            completedWorkerId,
            failedWorkerId,
            queuedWorkerId,
            queuedWorkerId);
        var workers = new RecordingWorkerOperations();
        var session = new TestWorkSystemSession(workers);
        var snapshots = CreateSnapshotSource(new Dictionary<WorkerId, Queue<WorkerSnapshot?>>
        {
            [transitioningWorkerId] = new([
                CreateSnapshot(transitioningWorkerId, WorkerState.Pausing),
                CreateSnapshot(transitioningWorkerId, WorkerState.Running),
            ]),
            [pausedWorkerId] = new([CreateSnapshot(pausedWorkerId, WorkerState.Paused)]),
            [completedWorkerId] = new([CreateSnapshot(completedWorkerId, WorkerState.Completed)]),
            [failedWorkerId] = new([CreateSnapshot(failedWorkerId, WorkerState.Failed)]),
            [queuedWorkerId] = new([CreateSnapshot(queuedWorkerId, WorkerState.Queued)]),
        });

        await WorkflowExecutionSupport.PauseOutstandingChildren(run, session, snapshots, CancellationToken.None);

        Assert.Collection(
            workers.Executions,
            execution =>
            {
                Assert.Equal(transitioningWorkerId, execution.Worker.WorkerId);
                Assert.Equal(WorkAction.Pause, execution.Action);
            },
            execution =>
            {
                Assert.Equal(queuedWorkerId, execution.Worker.WorkerId);
                Assert.Equal(WorkAction.Pause, execution.Action);
            });
    }

    [Fact]
    public async Task ResumeOutstandingChildrenRetriesPausedWorkersUntilTheyRestart()
    {
        var pausedWorkerId = WorkerId.New();
        var queuedWorkerId = WorkerId.New();
        var failedWorkerId = WorkerId.New();
        var run = CreateRunWithOutstandingWorkers(pausedWorkerId, queuedWorkerId, failedWorkerId);
        var workers = new RecordingWorkerOperations(new Dictionary<WorkerId, Queue<WorkActionOutcome>>
        {
            [pausedWorkerId] = new([
                WorkActionOutcome.Conflict(
                    WorkAction.Start,
                    CreateSnapshot(pausedWorkerId, WorkerState.Paused, revision: 1),
                    [WorkMessage.Error("workable.worker.conflict", "The worker changed.", "worker.revision")]),
                WorkActionOutcome.Accepted(
                    WorkAction.Start,
                    CreateSnapshot(pausedWorkerId, WorkerState.Running, revision: 2),
                    []),
            ]),
            [queuedWorkerId] = new([
                WorkActionOutcome.Accepted(
                    WorkAction.Start,
                    CreateSnapshot(queuedWorkerId, WorkerState.Running, revision: 1),
                    []),
            ]),
        });
        var session = new TestWorkSystemSession(workers);
        var snapshots = CreateSnapshotSource(new Dictionary<WorkerId, Queue<WorkerSnapshot?>>
        {
            [pausedWorkerId] = new([
                CreateSnapshot(pausedWorkerId, WorkerState.Paused, revision: 1),
                CreateSnapshot(pausedWorkerId, WorkerState.Paused, revision: 2),
            ]),
            [queuedWorkerId] = new([CreateSnapshot(queuedWorkerId, WorkerState.Queued, revision: 1)]),
            [failedWorkerId] = new([CreateSnapshot(failedWorkerId, WorkerState.Failed, revision: 1)]),
        });

        await WorkflowExecutionSupport.ResumeOutstandingChildren(run, session, snapshots, CancellationToken.None);

        Assert.Collection(
            workers.Executions,
            execution =>
            {
                Assert.Equal(pausedWorkerId, execution.Worker.WorkerId);
                Assert.Equal(1, execution.Worker.Revision);
                Assert.Equal(WorkAction.Start, execution.Action);
            },
            execution =>
            {
                Assert.Equal(pausedWorkerId, execution.Worker.WorkerId);
                Assert.Equal(2, execution.Worker.Revision);
                Assert.Equal(WorkAction.Start, execution.Action);
            },
            execution =>
            {
                Assert.Equal(queuedWorkerId, execution.Worker.WorkerId);
                Assert.Equal(1, execution.Worker.Revision);
                Assert.Equal(WorkAction.Start, execution.Action);
            });
    }

    private static RegisteredWorkflow CreateWorkflow(params WorkflowStepDefinition[] steps)
        => new(
            WorkflowDefinition.Create("workflow.execution.support"),
            steps,
            WorkOperateAuthorizationConfiguration.None);

    private static WorkflowRunState CreateRunWithOutstandingWorkers(params WorkerId[] workerIds)
    {
        var run = WorkflowRunState.Create(
            CreateWorkflow(Dispatch("dispatch", "sample.dispatch")),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        run.MarkStepCompleted("dispatch", workerIds);
        return run;
    }

    private static Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>> CreateSnapshotSource(
        IReadOnlyDictionary<WorkerId, Queue<WorkerSnapshot?>> snapshots)
        => (workerId, _) =>
        {
            if (!snapshots.TryGetValue(workerId, out var queue) || queue.Count == 0)
            {
                return Task.FromResult<WorkerSnapshot?>(null);
            }

            var next = queue.Count > 1
                ? queue.Dequeue()
                : queue.Peek();
            return Task.FromResult(next);
        };

    private static WorkerSnapshot CreateSnapshot(
        WorkerId workerId,
        WorkerState state,
        long revision = 1)
        => new(
            workerId,
            Revision: revision,
            StateSequence: revision,
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

    private sealed class TestWorkSystemSession(IWorkerOperations workers) : IWorkSystemSession
    {
        public string? SystemName => "workflow-tests";

        public WorkSystemState SystemState => WorkSystemState.Started;

        public WorkSystemCapabilities Capabilities => WorkSystemCapabilities.None;

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue => throw new NotSupportedException();

        public IWorkerOperations Workers { get; } = workers;

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();
    }

    private sealed class RecordingWorkerOperations(
        IReadOnlyDictionary<WorkerId, Queue<WorkActionOutcome>>? outcomes = null) : IWorkerOperations
    {
        public List<(WorkerVersion Worker, WorkAction Action)> Executions { get; } = [];

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkAction action,
            CancellationToken cancellationToken = default)
        {
            this.Executions.Add((worker, action));
            if (outcomes is not null &&
                outcomes.TryGetValue(worker.WorkerId, out var queuedOutcomes) &&
                queuedOutcomes.Count > 0)
            {
                return Task.FromResult(queuedOutcomes.Dequeue());
            }

            var nextState = action switch
            {
                WorkAction.Cancel => WorkerState.Canceled,
                WorkAction.Pause => WorkerState.Paused,
                WorkAction.Start => WorkerState.Running,
                _ => WorkerState.Completed,
            };
            return Task.FromResult(WorkActionOutcome.Accepted(action, CreateSnapshot(worker.WorkerId, nextState, worker.Revision), []));
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

    private static DispatchWorkflowStepDefinition Dispatch(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null)
        => new(stepName, WorkDefinition.Create(workDefinitionName), input);
}

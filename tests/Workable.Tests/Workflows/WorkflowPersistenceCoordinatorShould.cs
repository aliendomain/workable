using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowPersistenceCoordinatorShould
{
    [Fact]
    public async Task InitializeForwardsWorkflowDefinitionsToTheStore()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            WorkSystemId.New(),
            "workflow-persistence-tests");
        var definitions = new[]
        {
            WorkflowDefinition.Create("workflow.one"),
            WorkflowDefinition.Create("workflow.two"),
        };

        await coordinator.Initialize(definitions, CancellationToken.None);

        var initialization = Assert.Single(store.WorkflowInitializations);
        Assert.Equal("workflow-persistence-tests", initialization.WorkSystemName);
        Assert.Equal(2, initialization.Definitions.Count);
        Assert.Equal("workflow.one", initialization.Definitions[0].Name);
        Assert.Equal("workflow.two", initialization.Definitions[1].Name);
    }

    [Fact]
    public async Task ListIncompleteRunsForwardsTheSystemIdentity()
    {
        var systemId = WorkSystemId.New();
        var store = new RecordingWorkflowPersistenceStore
        {
            IncompleteRuns =
            [
                CreateRun(systemId, "workflow-persistence-tests", "workflow.one"),
            ],
        };
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            systemId,
            "workflow-persistence-tests");

        var runs = new List<WorkflowRunPersistenceRecord>();
        await foreach (var run in coordinator.ListIncompleteRuns(CancellationToken.None))
        {
            runs.Add(run);
        }

        var request = Assert.Single(store.WorkflowReadRequests);
        Assert.Equal(systemId, request.WorkSystemId);
        Assert.Equal("workflow-persistence-tests", request.WorkSystemName);
        Assert.Single(runs);
        Assert.Equal("workflow.one", runs[0].DefinitionName);
    }

    [Fact]
    public async Task UpsertAndDeleteForwardToTheStore()
    {
        var systemId = WorkSystemId.New();
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            systemId,
            "workflow-persistence-tests");
        var run = CreateRun(systemId, "workflow-persistence-tests", "workflow.one");

        await coordinator.UpsertRun(run, CancellationToken.None);
        await coordinator.DeleteRun(run.RunId, CancellationToken.None);

        Assert.Equal(run, Assert.Single(store.UpsertedRuns));
        Assert.Equal(run.RunId, Assert.Single(store.DeletedRuns).RunId);
    }

    [Fact]
    public async Task PersistRunAndDispatchUsesOneTransactionForRunAndWorkerEnqueue()
    {
        var systemId = WorkSystemId.New();
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            systemId,
            "workflow-persistence-tests");
        var run = CreateRun(systemId, "workflow-persistence-tests", "workflow.one");
        WorkerOptions? dispatchOptions = null;

        await coordinator.PersistRunAndDispatch(
            run,
            (options, _) =>
            {
                dispatchOptions = options;
                store.RecordDispatch(options);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
        Assert.Same(transaction, Assert.IsAssignableFrom<IWorkflowPersistenceTransaction>(dispatchOptions!.QueueDurabilityTransaction));
        Assert.Equal(transaction.Id, Assert.Single(store.TransactionalUpserts).TransactionId);
        Assert.Equal(transaction.Id, Assert.Single(store.Dispatches).TransactionId);
    }

    [Fact]
    public async Task AdvanceRunAndDispatchUsesOneTransactionForCursorAdvanceAndNextDispatch()
    {
        var systemId = WorkSystemId.New();
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            systemId,
            "workflow-persistence-tests");
        var run = CreateRun(systemId, "workflow-persistence-tests", "workflow.one") with
        {
            Steps =
            [
                new WorkflowStepPersistenceRecord(
                    "join",
                    WorkflowStepKind.Join,
                    WorkflowStepRunStatus.Completed,
                    [],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    []),
            ],
        };

        await coordinator.AdvanceRunAndDispatch(
            run,
            (options, _) =>
            {
                store.RecordDispatch(options);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
        Assert.Equal(transaction.Id, Assert.Single(store.TransactionalUpserts).TransactionId);
        Assert.Equal(transaction.Id, Assert.Single(store.Dispatches).TransactionId);
    }

    [Fact]
    public async Task CompleteAndDeleteRunUsesOneTransactionForFinalizationAndCleanup()
    {
        var systemId = WorkSystemId.New();
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            systemId,
            "workflow-persistence-tests");
        var run = CreateRun(systemId, "workflow-persistence-tests", "workflow.one") with
        {
            Status = WorkflowRunStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await coordinator.CompleteAndDeleteRun(run, CancellationToken.None);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
        Assert.Equal(transaction.Id, Assert.Single(store.TransactionalUpserts).TransactionId);
        Assert.Equal(transaction.Id, Assert.Single(store.TransactionalDeletes).TransactionId);
    }

    private static WorkflowRunPersistenceRecord CreateRun(
        WorkSystemId systemId,
        string? systemName,
        string definitionName)
        => new(
            systemId,
            systemName,
            WorkflowRunId.New(),
            WorkflowDefinition.Create(definitionName).Version,
            definitionName,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkflowRunStatus.Running,
            [new WorkflowStepPersistenceRecord(
                "dispatch",
                WorkflowStepKind.DispatchWork,
                WorkflowStepRunStatus.Running,
                [],
                DateTimeOffset.UtcNow,
                null,
                [])],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            []);

    private sealed class RecordingWorkflowPersistenceStore : IWorkPersistenceStore
    {
        public List<WorkflowPersistenceInitializationContext> WorkflowInitializations { get; } = [];

        public List<WorkflowPersistenceReadRequest> WorkflowReadRequests { get; } = [];

        public List<WorkflowRunPersistenceRecord> UpsertedRuns { get; } = [];

        public List<WorkflowPersistenceDeleteRequest> DeletedRuns { get; } = [];

        public List<(Guid TransactionId, WorkflowRunPersistenceRecord Run)> TransactionalUpserts { get; } = [];

        public List<(Guid TransactionId, WorkflowPersistenceDeleteRequest Request)> TransactionalDeletes { get; } = [];

        public List<(Guid TransactionId, WorkerOptions Options)> Dispatches { get; } = [];

        public List<RecordingWorkflowPersistenceTransaction> Transactions { get; } = [];

        public IReadOnlyList<WorkflowRunPersistenceRecord> IncompleteRuns { get; init; } = [];

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

        public Task InitializeWorkflows(
            WorkflowPersistenceInitializationContext context,
            CancellationToken cancellationToken = default)
        {
            this.WorkflowInitializations.Add(context);
            return Task.CompletedTask;
        }

        public Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
            WorkflowPersistenceTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            var transaction = new RecordingWorkflowPersistenceTransaction();
            this.Transactions.Add(transaction);
            return Task.FromResult<IWorkflowPersistenceTransaction>(transaction);
        }

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListIncompleteWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.WorkflowReadRequests.Add(request);
            foreach (var run in this.IncompleteRuns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return run;
            }

            await Task.CompletedTask;
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
        {
            this.UpsertedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            this.TransactionalUpserts.Add((((RecordingWorkflowPersistenceTransaction)transaction).Id, run));
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            this.DeletedRuns.Add(request);
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            this.TransactionalDeletes.Add((((RecordingWorkflowPersistenceTransaction)transaction).Id, request));
            return Task.CompletedTask;
        }

        public void RecordDispatch(WorkerOptions options)
        {
            var transaction = Assert.IsType<RecordingWorkflowPersistenceTransaction>(options.QueueDurabilityTransaction);
            this.Dispatches.Add((transaction.Id, options));
        }

        public sealed class RecordingWorkflowPersistenceTransaction : IWorkflowPersistenceTransaction
        {
            public Guid Id { get; } = Guid.NewGuid();

            public bool Committed { get; private set; }

            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

            public Task Commit(CancellationToken cancellationToken = default)
            {
                this.Committed = true;
                return Task.CompletedTask;
            }
        }
    }
}

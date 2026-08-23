using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowPersistenceCoordinatorShould
{
    [Fact]
    public async Task SkipPersistenceWhenWorkflowOrSystemIdentityIsMissing()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var named = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var unnamed = new WorkflowPersistenceCoordinator(store, workSystemName: " ");

        await named.Initialize([], CancellationToken.None);
        await unnamed.Initialize(
            [WorkflowDefinition.Create(
                "workflow.durable.unnamed",
                coordination: WorkflowCoordinationConfiguration.Durable)],
            CancellationToken.None);
        var unnamedRuns = new List<WorkflowRunPersistenceRecord>();
        await foreach (var run in unnamed.ListRuns(CancellationToken.None))
        {
            unnamedRuns.Add(run);
        }
        IWorkflowPersistenceTransaction? observedTransaction = null;
        var transactionActionInvoked = false;
        await unnamed.ExecuteTransaction(
            WorkflowRunId.New(),
            (transaction, options, _) =>
            {
                transactionActionInvoked = true;
                observedTransaction = transaction;
                Assert.Equal(WorkerOptions.Default, options);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Empty(store.WorkflowInitializations);
        Assert.Empty(store.WorkflowReadRequests);
        Assert.Empty(unnamedRuns);
        Assert.True(transactionActionInvoked);
        Assert.Null(observedTransaction);
    }

    [Fact]
    public async Task InitializeForwardsOnlyDurableWorkflowDefinitionsToTheStore()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");
        var definitions = new[]
        {
            WorkflowDefinition.Create("workflow.one"),
            WorkflowDefinition.Create(
                "workflow.two",
                coordination: WorkflowCoordinationConfiguration.Durable),
        };

        await coordinator.Initialize(definitions, CancellationToken.None);

        var initialization = Assert.Single(store.WorkflowInitializations);
        Assert.Equal("workflow-persistence-tests", initialization.WorkSystemName);
        Assert.Single(initialization.Definitions);
        Assert.Equal("workflow.two", initialization.Definitions[0].Name);
    }

    [Fact]
    public async Task InitializeDoesNothingWhenNoDurableWorkflowDefinitionsExist()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");

        await coordinator.Initialize(
            [
                WorkflowDefinition.Create("workflow.one"),
                WorkflowDefinition.Create("workflow.two"),
            ],
            CancellationToken.None);

        Assert.Empty(store.WorkflowInitializations);
    }

    [Fact]
    public async Task ListRunsForwardsTheSystemIdentity()
    {
        var store = new RecordingWorkflowPersistenceStore
        {
            IncompleteRuns =
            [
                CreateRun("workflow-persistence-tests", "workflow.one"),
            ],
        };
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");

        var runs = new List<WorkflowRunPersistenceRecord>();
        await foreach (var run in coordinator.ListRuns(CancellationToken.None))
        {
            runs.Add(run);
        }

        var request = Assert.Single(store.WorkflowReadRequests);
        Assert.Equal("workflow-persistence-tests", request.WorkSystemName);
        Assert.Single(runs);
        Assert.Equal("workflow.one", runs[0].DefinitionName);
    }

    [Fact]
    public async Task UpsertAndDeleteForwardToTheStore()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        await coordinator.UpsertRun(run, CancellationToken.None);
        await coordinator.DeleteRun(run.RunId, CancellationToken.None);

        Assert.Equal(run, Assert.Single(store.UpsertedRuns));
        Assert.Equal(run.RunId, Assert.Single(store.DeletedRuns).RunId);
    }

    [Fact]
    public async Task SerializeRunWritesAndCreateSnapshotsAfterEnteringTheRunGate()
    {
        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeWrites = 0;
        var maximumActiveWrites = 0;
        var store = new RecordingWorkflowPersistenceStore
        {
            UpsertHandler = async (_, _) =>
            {
                var active = Interlocked.Increment(ref activeWrites);
                var observedMaximum = Volatile.Read(ref maximumActiveWrites);
                while (active > observedMaximum &&
                    Interlocked.CompareExchange(ref maximumActiveWrites, active, observedMaximum) != observedMaximum)
                {
                    observedMaximum = Volatile.Read(ref maximumActiveWrites);
                }
                if (firstWriteEntered.TrySetResult())
                {
                    await releaseFirstWrite.Task;
                }

                Interlocked.Decrement(ref activeWrites);
            },
        };
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");
        var status = WorkflowRunStatus.Running;

        var first = coordinator.UpsertRun(run.RunId, () => run with { Status = status }, CancellationToken.None);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        status = WorkflowRunStatus.Completed;
        var second = coordinator.UpsertRun(run.RunId, () => run with { Status = status }, CancellationToken.None);
        releaseFirstWrite.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActiveWrites);
        Assert.Equal(
            [WorkflowRunStatus.Running, WorkflowRunStatus.Completed],
            store.UpsertedRuns.Select(record => record.Status));
    }

    [Fact]
    public async Task HoldTheRunGateUntilATransactionCommits()
    {
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingWorkflowPersistenceStore
        {
            TransactionCommitHandler = async _ =>
            {
                commitEntered.TrySetResult();
                await releaseCommit.Task;
            },
        };
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        var transactionalWrite = coordinator.ExecuteTransaction(
            run.RunId,
            (transaction, _, cancellationToken) => coordinator.UpsertRun(
                run.RunId,
                () => run with { Status = WorkflowRunStatus.Running },
                transaction,
                cancellationToken),
            CancellationToken.None);
        await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var laterWrite = coordinator.UpsertRun(
            run.RunId,
            () => run with { Status = WorkflowRunStatus.Completed },
            CancellationToken.None);

        Assert.False(laterWrite.IsCompleted);
        releaseCommit.TrySetResult();
        await Task.WhenAll(transactionalWrite, laterWrite);

        Assert.Equal(
            [WorkflowRunStatus.Running, WorkflowRunStatus.Completed],
            store.CommittedRuns.Select(record => record.Status));
    }

    [Fact]
    public async Task CoalesceConcurrentReceiptCheckpointsForOneRun()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");
        var checkpoints = Enumerable.Range(0, 64)
            .Select(_ => coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(checkpoints);

        Assert.InRange(store.UpsertedRuns.Count, 1, 2);
    }

    [Fact]
    public async Task PropagateCoalescedWriteFailuresAndAcceptALaterCheckpoint()
    {
        var attempts = 0;
        var store = new RecordingWorkflowPersistenceStore
        {
            UpsertHandler = (_, _) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("checkpoint failed"))
                : Task.CompletedTask,
        };
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None));
        await coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None);

        Assert.Equal("checkpoint failed", exception.Message);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PropagateCriticalCoalescedWriteFailuresAndAcceptALaterCheckpoint()
    {
        var attempts = 0;
        var store = new RecordingWorkflowPersistenceStore
        {
            UpsertHandler = (_, _) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new BadImageFormatException("Critical checkpoint failure."))
                : Task.CompletedTask,
        };
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        var exception = await Assert.ThrowsAsync<BadImageFormatException>(async () =>
            await coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1)));
        await coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("Critical checkpoint failure.", exception.Message);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ContinueCoalescedPumpWhenCheckpointArrivesDuringAWrite()
    {
        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new RecordingWorkflowPersistenceStore
        {
            UpsertHandler = async (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstWriteEntered.TrySetResult();
                    await releaseFirstWrite.Task;
                }
            },
        };
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        var first = coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None);
        releaseFirstWrite.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DrainAcceptedCoalescedCheckpointBeforeDeletingRun()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        var checkpoint = coordinator.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None);
        var deletion = coordinator.DeleteRun(run.RunId, CancellationToken.None);
        await Task.WhenAll(checkpoint, deletion);

        Assert.Equal(["upsert", "delete"], store.RunOperations);
        Assert.Equal(run, Assert.Single(store.CommittedRuns));
        Assert.Equal(run.RunId, Assert.Single(store.DeletedRuns).RunId);
    }

    [Fact]
    public async Task CoalescedUpsertHonorsMissingStoreAndCanceledCaller()
    {
        var run = CreateRun("workflow-persistence-tests", "workflow.one");
        var unavailable = new WorkflowPersistenceCoordinator(null, "workflow-persistence-tests");
        await unavailable.UpsertRunCoalesced(run.RunId, () => run, CancellationToken.None);

        var available = new WorkflowPersistenceCoordinator(
            new RecordingWorkflowPersistenceStore(),
            "workflow-persistence-tests");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            available.UpsertRunCoalesced(run.RunId, () => run, cancellation.Token));
    }

    [Fact]
    public async Task BoundParallelismInTheDefaultDurableWorkerExistenceFallback()
    {
        var active = 0;
        var maximumActive = 0;
        var store = new RecordingWorkflowPersistenceStore
        {
            DurableWorkerExistsHandler = async (_, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                var observedMaximum = Volatile.Read(ref maximumActive);
                while (current > observedMaximum &&
                    Interlocked.CompareExchange(ref maximumActive, current, observedMaximum) != observedMaximum)
                {
                    observedMaximum = Volatile.Read(ref maximumActive);
                }

                await Task.Delay(10, cancellationToken);
                Interlocked.Decrement(ref active);
                return true;
            },
        };
        var coordinator = new WorkflowPersistenceCoordinator(store, "workflow-persistence-tests");
        var workerIds = Enumerable.Range(0, 64).Select(_ => WorkerId.New()).ToArray();

        var existing = await coordinator.DurableWorkersExist(workerIds, CancellationToken.None);

        Assert.Equal(workerIds.OrderBy(id => id.Value), existing.OrderBy(id => id.Value));
        Assert.InRange(maximumActive, 2, 16);
    }

    [Fact]
    public async Task ListRunsWithoutStoreReturnsAnEmptySequence()
    {
        var coordinator = new WorkflowPersistenceCoordinator(
            store: null,
            "workflow-persistence-tests");
        var runs = new List<WorkflowRunPersistenceRecord>();

        await foreach (var run in coordinator.ListRuns(CancellationToken.None))
        {
            runs.Add(run);
        }

        Assert.Empty(runs);
    }

    [Fact]
    public async Task UpsertAndDeleteWithoutStoreAreNoOpsAndDurableWorkerChecksReturnFalse()
    {
        var coordinator = new WorkflowPersistenceCoordinator(
            store: null,
            "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        await coordinator.UpsertRun(run, CancellationToken.None);
        await coordinator.DeleteRun(run.RunId, CancellationToken.None);
        var exists = await coordinator.DurableWorkerExists(WorkerId.New(), CancellationToken.None);

        Assert.False(exists);
    }

    [Fact]
    public async Task ExecuteTransactionWithoutStoreUsesDefaultOptionsAndNullTransaction()
    {
        var coordinator = new WorkflowPersistenceCoordinator(
            store: null,
            "workflow-persistence-tests");
        IWorkflowPersistenceTransaction? observedTransaction = null;
        WorkerOptions? observedOptions = null;

        await coordinator.ExecuteTransaction(
            WorkflowRunId.New(),
            (transaction, options, _) =>
            {
                observedTransaction = transaction;
                observedOptions = options;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(observedTransaction);
        Assert.Equal(WorkerOptions.Default, observedOptions);
    }

    [Fact]
    public async Task UpsertRunWithNullTransactionFallsBackToTheNonTransactionalStoreMethod()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        await coordinator.UpsertRun(run, transaction: null, CancellationToken.None);

        Assert.Equal(run, Assert.Single(store.UpsertedRuns));
        Assert.Empty(store.TransactionalUpserts);
    }

    [Fact]
    public async Task DeleteRunWithNullTransactionFallsBackToTheNonTransactionalStoreMethod()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        await coordinator.DeleteRun(run.RunId, transaction: null, CancellationToken.None);

        Assert.Equal(run.RunId, Assert.Single(store.DeletedRuns).RunId);
        Assert.Empty(store.TransactionalDeletes);
    }

    [Fact]
    public async Task ExecuteTransactionUsesOneTransactionForRunUpsertAndWorkerEnqueue()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");
        WorkerOptions? dispatchOptions = null;

        await coordinator.ExecuteTransaction(
            run.RunId,
            async (transaction, options, transactionCancellationToken) =>
            {
                await coordinator.UpsertRun(run, transaction, transactionCancellationToken);
                dispatchOptions = options;
                store.RecordDispatch(options);
                await Task.CompletedTask;
            },
            CancellationToken.None);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
        Assert.Same(transaction, Assert.IsAssignableFrom<IWorkflowPersistenceTransaction>(dispatchOptions!.QueueDurabilityTransaction));
        Assert.Equal(transaction.Id, Assert.Single(store.TransactionalUpserts).TransactionId);
        Assert.Equal(transaction.Id, Assert.Single(store.Dispatches).TransactionId);
    }

    [Fact]
    public async Task ExecuteTransactionDoesNotCommitWhenActionThrows()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteTransaction(
                WorkflowRunId.New(),
                (_, _, _) => throw new InvalidOperationException("transaction failed"),
                CancellationToken.None));

        var transaction = Assert.Single(store.Transactions);
        Assert.False(transaction.Committed);
    }

    [Fact]
    public async Task DeleteRunWithinTransactionUsesOneTransactionForCleanup()
    {
        var store = new RecordingWorkflowPersistenceStore();
        var coordinator = new WorkflowPersistenceCoordinator(
            store,
            "workflow-persistence-tests");
        var run = CreateRun("workflow-persistence-tests", "workflow.one");

        await coordinator.ExecuteTransaction(
            run.RunId,
            (transaction, _, transactionCancellationToken) =>
                coordinator.DeleteRun(run.RunId, transaction, transactionCancellationToken),
            CancellationToken.None);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
        Assert.Equal(transaction.Id, Assert.Single(store.TransactionalDeletes).TransactionId);
    }

    private static WorkflowRunPersistenceRecord CreateRun(
        string? systemName,
        string definitionName)
        => new(
            systemName,
            WorkflowRunId.New(),
            WorkflowDefinition.Create(definitionName).Version,
            definitionName,
            null,
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
            [],
            []);

    private sealed class RecordingWorkflowPersistenceStore : IWorkPersistenceStore
    {
        public List<WorkflowPersistenceInitializationContext> WorkflowInitializations { get; } = [];

        public List<WorkflowPersistenceReadRequest> WorkflowReadRequests { get; } = [];

        public List<WorkflowRunPersistenceRecord> UpsertedRuns { get; } = [];

        public List<WorkflowRunPersistenceRecord> CommittedRuns { get; } = [];

        public List<WorkflowPersistenceDeleteRequest> DeletedRuns { get; } = [];

        public List<(Guid TransactionId, WorkflowRunPersistenceRecord Run)> TransactionalUpserts { get; } = [];

        public List<(Guid TransactionId, WorkflowPersistenceDeleteRequest Request)> TransactionalDeletes { get; } = [];

        public List<(Guid TransactionId, WorkerOptions Options)> Dispatches { get; } = [];

        public List<RecordingWorkflowPersistenceTransaction> Transactions { get; } = [];

        public ConcurrentQueue<string> RunOperations { get; } = [];

        public IReadOnlyList<WorkflowRunPersistenceRecord> IncompleteRuns { get; init; } = [];

        public Func<WorkflowRunPersistenceRecord, CancellationToken, Task>? UpsertHandler { get; init; }

        public Func<CancellationToken, Task>? TransactionCommitHandler { get; init; }

        public Func<WorkerId, CancellationToken, Task<bool>>? DurableWorkerExistsHandler { get; init; }

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

        public Task<bool> DurableWorkerExists(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
            => this.DurableWorkerExistsHandler?.Invoke(workerId, cancellationToken)
                ?? Task.FromResult(false);

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
            var transaction = new RecordingWorkflowPersistenceTransaction(
                this.CommittedRuns,
                this.TransactionCommitHandler);
            this.Transactions.Add(transaction);
            return Task.FromResult<IWorkflowPersistenceTransaction>(transaction);
        }

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
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

        public async Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
        {
            this.RunOperations.Enqueue("upsert");
            this.UpsertedRuns.Add(run);
            if (this.UpsertHandler is not null)
            {
                await this.UpsertHandler(run, cancellationToken);
            }

            this.CommittedRuns.Add(run);
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            var recordingTransaction = (RecordingWorkflowPersistenceTransaction)transaction;
            this.TransactionalUpserts.Add((recordingTransaction.Id, run));
            recordingTransaction.Stage(run);
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            this.RunOperations.Enqueue("delete");
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

        public sealed class RecordingWorkflowPersistenceTransaction(
            List<WorkflowRunPersistenceRecord> committedRuns,
            Func<CancellationToken, Task>? commitHandler) : IWorkflowPersistenceTransaction
        {
            private readonly List<WorkflowRunPersistenceRecord> pendingRuns = [];

            public Guid Id { get; } = Guid.NewGuid();

            public bool Committed { get; private set; }

            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

            public void Stage(WorkflowRunPersistenceRecord run)
                => this.pendingRuns.Add(run);

            public async Task Commit(CancellationToken cancellationToken = default)
            {
                if (commitHandler is not null)
                {
                    await commitHandler(cancellationToken);
                }

                committedRuns.AddRange(this.pendingRuns);
                this.Committed = true;
            }
        }
    }
}

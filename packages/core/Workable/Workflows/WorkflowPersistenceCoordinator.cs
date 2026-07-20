using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkflowPersistenceCoordinator(
    IWorkPersistenceStore? store,
    string? workSystemName)
{
    private readonly IWorkPersistenceStore? store = store;
    private readonly string? workSystemName = workSystemName;
    private readonly Lock runGatesSync = new();
    private readonly Dictionary<WorkflowRunId, RunGate> runGates = [];
    private readonly ConcurrentDictionary<WorkflowRunId, CoalescedUpsertState> coalescedUpserts = new();

    public bool IsAvailable => this.store is not null;

    public Task Initialize(
        IReadOnlyList<WorkflowDefinition> definitions,
        CancellationToken cancellationToken)
    {
        if (definitions.Count == 0 || string.IsNullOrWhiteSpace(this.workSystemName))
        {
            return Task.CompletedTask;
        }

        var durableDefinitions = definitions
            .Where(definition => definition.Coordination.IsDurable)
            .ToArray();
        if (durableDefinitions.Length == 0)
        {
            return Task.CompletedTask;
        }

        return this.store?.InitializeWorkflows(
            new WorkflowPersistenceInitializationContext(
                this.workSystemName,
                durableDefinitions),
            cancellationToken)
            ?? Task.CompletedTask;
    }

    public IAsyncEnumerable<WorkflowRunPersistenceRecord> ListRuns(CancellationToken cancellationToken)
        => this.store is not null && !string.IsNullOrWhiteSpace(this.workSystemName)
            ? this.store.ListWorkflowRuns(
                new WorkflowPersistenceReadRequest(this.workSystemName),
                cancellationToken)
            : Empty();

    public Task UpsertRun(WorkflowRunPersistenceRecord run, CancellationToken cancellationToken)
        => this.RunExclusive(
            run.RunId,
            () => this.store?.UpsertWorkflowRun(run, cancellationToken) ?? Task.CompletedTask,
            cancellationToken);

    public Task UpsertRun(
        WorkflowRunId runId,
        Func<WorkflowRunPersistenceRecord> createRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createRun);
        return this.RunExclusive(
            runId,
            () => this.store?.UpsertWorkflowRun(createRun(), cancellationToken) ?? Task.CompletedTask,
            cancellationToken);
    }

    public Task UpsertRunAndApply(
        WorkflowRunId runId,
        Func<WorkflowRunPersistenceRecord> createRun,
        Action applyPersistedState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createRun);
        ArgumentNullException.ThrowIfNull(applyPersistedState);
        return this.RunExclusive(
            runId,
            async () =>
            {
                if (this.store is not null)
                {
                    await this.store.UpsertWorkflowRun(createRun(), cancellationToken);
                }

                applyPersistedState();
            },
            cancellationToken);
    }

    public Task UpsertRunCoalesced(
        WorkflowRunId runId,
        Func<WorkflowRunPersistenceRecord> createRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createRun);
        if (this.store is null)
        {
            return Task.CompletedTask;
        }

        var state = this.coalescedUpserts.GetOrAdd(
            runId,
            _ => new CoalescedUpsertState(this, runId));
        return state.Enqueue(createRun, cancellationToken);
    }

    public Task UpsertRun(
        WorkflowRunPersistenceRecord run,
        IWorkflowPersistenceTransaction? transaction,
        CancellationToken cancellationToken)
        => this.UpsertRunCore(run, transaction, cancellationToken);

    public Task UpsertRun(
        WorkflowRunId runId,
        Func<WorkflowRunPersistenceRecord> createRun,
        IWorkflowPersistenceTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createRun);
        return transaction is null
            ? this.UpsertRun(runId, createRun, cancellationToken)
            : this.store!.UpsertWorkflowRun(createRun(), transaction, cancellationToken);
    }

    public async Task DeleteRun(WorkflowRunId runId, CancellationToken cancellationToken)
    {
        this.coalescedUpserts.TryGetValue(runId, out var coalescedUpsert);
        if (coalescedUpsert is not null)
        {
            await coalescedUpsert.StopAndDrain();
        }

        await this.RunExclusive(
            runId,
            () => this.store?.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(runId), cancellationToken)
                ?? Task.CompletedTask,
            cancellationToken);
        if (coalescedUpsert is not null)
        {
            this.coalescedUpserts.TryRemove(
                new KeyValuePair<WorkflowRunId, CoalescedUpsertState>(runId, coalescedUpsert));
        }
    }

    public Task DeleteRun(
        WorkflowRunId runId,
        IWorkflowPersistenceTransaction? transaction,
        CancellationToken cancellationToken)
        => this.DeleteRunCore(runId, transaction, cancellationToken);

    public Task<bool> DurableWorkerExists(WorkerId workerId, CancellationToken cancellationToken)
        => this.store?.DurableWorkerExists(workerId, cancellationToken)
        ?? Task.FromResult(false);

    public Task<IReadOnlySet<WorkerId>> DurableWorkersExist(
        IReadOnlyCollection<WorkerId> workerIds,
        CancellationToken cancellationToken)
        => this.store?.DurableWorkersExist(workerIds, cancellationToken)
        ?? Task.FromResult<IReadOnlySet<WorkerId>>(new HashSet<WorkerId>());

    public Task ExecuteTransaction(
        WorkflowRunId runId,
        Func<IWorkflowPersistenceTransaction?, WorkerOptions, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => this.store is null
            ? this.ExecuteInTransaction(action, cancellationToken)
            : this.RunExclusive(
                runId,
                () => this.ExecuteInTransaction(action, cancellationToken),
                cancellationToken);

    private async Task RunExclusive(
        WorkflowRunId runId,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        RunGate gate;
        lock (this.runGatesSync)
        {
            if (!this.runGates.TryGetValue(runId, out gate!))
            {
                gate = new RunGate();
                this.runGates[runId] = gate;
            }

            gate.References++;
        }

        try
        {
            await gate.Sync.WaitAsync(cancellationToken);
            try
            {
                await action();
            }
            finally
            {
                gate.Sync.Release();
            }
        }
        finally
        {
            lock (this.runGatesSync)
            {
                gate.References--;
                if (gate.References == 0)
                {
                    this.runGates.Remove(runId);
                }
            }
        }
    }

    private sealed class RunGate
    {
        public SemaphoreSlim Sync { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class CoalescedUpsertState(
        WorkflowPersistenceCoordinator owner,
        WorkflowRunId runId)
    {
        private static readonly TimeSpan CoalescingInterval = TimeSpan.FromMilliseconds(5);
        private readonly Lock sync = new();
        private readonly List<TaskCompletionSource> waiters = [];
        private readonly TaskCompletionSource drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<WorkflowRunPersistenceRecord>? createRun;
        private bool isRunning;
        private bool isStopped;

        public Task Enqueue(
            Func<WorkflowRunPersistenceRecord> createRun,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this.sync)
            {
                if (this.isStopped)
                {
                    return Task.CompletedTask;
                }

                this.createRun = createRun;
                this.waiters.Add(waiter);
                if (!this.isRunning)
                {
                    this.isRunning = true;
                    _ = this.Run();
                }
            }

            return waiter.Task.WaitAsync(cancellationToken);
        }

        public Task StopAndDrain()
        {
            lock (this.sync)
            {
                this.isStopped = true;
                if (!this.isRunning)
                {
                    this.drained.TrySetResult();
                }

                return this.drained.Task;
            }
        }

        private async Task Run()
        {
            while (true)
            {
                await Task.Delay(CoalescingInterval);

                Func<WorkflowRunPersistenceRecord> createRun;
                TaskCompletionSource[] waiters;
                lock (this.sync)
                {
                    createRun = this.createRun!;
                    waiters = [.. this.waiters];
                    this.waiters.Clear();
                }

                try
                {
                    await owner.RunExclusive(
                        runId,
                        () => owner.store!.UpsertWorkflowRun(createRun(), CancellationToken.None),
                        CancellationToken.None);
                    foreach (var waiter in waiters)
                    {
                        waiter.TrySetResult();
                    }
                }
                catch (Exception exception)
                {
                    foreach (var waiter in waiters)
                    {
                        waiter.TrySetException(exception);
                    }
                }

                lock (this.sync)
                {
                    if (this.waiters.Count == 0)
                    {
                        this.isRunning = false;
                        if (this.isStopped)
                        {
                            this.drained.TrySetResult();
                        }

                        return;
                    }
                }
            }
        }
    }

    private async Task UpsertRunCore(
        WorkflowRunPersistenceRecord run,
        IWorkflowPersistenceTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            await this.UpsertRun(run, cancellationToken);
            return;
        }

        await this.store!.UpsertWorkflowRun(run, transaction, cancellationToken);
    }

    private async Task DeleteRunCore(
        WorkflowRunId runId,
        IWorkflowPersistenceTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            await this.DeleteRun(runId, cancellationToken);
            return;
        }

        await this.store!.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(runId), transaction, cancellationToken);
    }

    private async Task ExecuteInTransaction(
        Func<IWorkflowPersistenceTransaction?, WorkerOptions, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (this.store is null || string.IsNullOrWhiteSpace(this.workSystemName))
        {
            await action(null, WorkerOptions.Default, cancellationToken);
            return;
        }

        await using var transaction = await this.store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest(this.workSystemName),
            cancellationToken);
        await action(
            transaction,
            WorkerOptions.Default with
            {
                QueueDurabilityTransaction = transaction,
            },
            cancellationToken);
        await transaction.Commit(cancellationToken);
    }

    private static async IAsyncEnumerable<WorkflowRunPersistenceRecord> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}

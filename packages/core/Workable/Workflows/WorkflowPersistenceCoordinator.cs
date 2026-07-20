namespace Workable;

internal sealed class WorkflowPersistenceCoordinator(
    IWorkPersistenceStore? store,
    string? workSystemName)
{
    private readonly IWorkPersistenceStore? store = store;
    private readonly string? workSystemName = workSystemName;

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
        => this.store?.UpsertWorkflowRun(run, cancellationToken)
        ?? Task.CompletedTask;

    public Task UpsertRun(
        WorkflowRunPersistenceRecord run,
        IWorkflowPersistenceTransaction? transaction,
        CancellationToken cancellationToken)
        => this.UpsertRunCore(run, transaction, cancellationToken);

    public Task DeleteRun(WorkflowRunId runId, CancellationToken cancellationToken)
        => this.store?.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(runId), cancellationToken)
        ?? Task.CompletedTask;

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
        Func<IWorkflowPersistenceTransaction?, WorkerOptions, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => this.ExecuteInTransaction(action, cancellationToken);

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

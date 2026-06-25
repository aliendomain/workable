namespace Workable;

internal sealed class WorkflowPersistenceCoordinator(
    IWorkPersistenceStore? store,
    WorkSystemId workSystemId,
    string? workSystemName)
{
    private readonly IWorkPersistenceStore? store = store;
    private readonly WorkSystemId workSystemId = workSystemId;
    private readonly string? workSystemName = workSystemName;

    public bool IsAvailable => this.store is not null;

    public Task Initialize(
        IReadOnlyList<WorkflowDefinition> definitions,
        CancellationToken cancellationToken)
    {
        if (definitions.Count == 0)
        {
            return Task.CompletedTask;
        }

        return this.store?.InitializeWorkflows(
            new WorkflowPersistenceInitializationContext(
                this.workSystemId,
                this.workSystemName,
                definitions),
            cancellationToken)
            ?? Task.CompletedTask;
    }

    public IAsyncEnumerable<WorkflowRunPersistenceRecord> ListIncompleteRuns(CancellationToken cancellationToken)
        => this.store?.ListIncompleteWorkflowRuns(
            new WorkflowPersistenceReadRequest(
                this.workSystemId,
                this.workSystemName),
            cancellationToken)
        ?? Empty();

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

        if (this.store is null)
        {
            await action(null, WorkerOptions.Default, cancellationToken);
            return;
        }

        await using var transaction = await this.store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest(
                this.workSystemId,
                this.workSystemName),
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

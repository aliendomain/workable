namespace Workable;

internal sealed class WorkflowPersistenceCoordinator(
    IWorkPersistenceStore? store,
    WorkSystemId workSystemId,
    string? workSystemName)
{
    private readonly IWorkPersistenceStore? store = store;
    private readonly WorkSystemId workSystemId = workSystemId;
    private readonly string? workSystemName = workSystemName;

    public Task Initialize(
        IReadOnlyList<WorkflowDefinition> definitions,
        CancellationToken cancellationToken)
        => this.store?.InitializeWorkflows(
            new WorkflowPersistenceInitializationContext(
                this.workSystemId,
                this.workSystemName,
                definitions),
            cancellationToken)
        ?? Task.CompletedTask;

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

    public Task DeleteRun(WorkflowRunId runId, CancellationToken cancellationToken)
        => this.store?.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(runId), cancellationToken)
        ?? Task.CompletedTask;

    public async Task PersistRunAndDispatch(
        WorkflowRunPersistenceRecord run,
        Func<WorkerOptions, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        await this.ExecuteInTransaction(
            async (transaction, transactionOptions, transactionCancellationToken) =>
            {
                await this.UpsertRun(run, transaction, transactionCancellationToken);
                await dispatch(transactionOptions, transactionCancellationToken);
            },
            cancellationToken);
    }

    public async Task AdvanceRunAndDispatch(
        WorkflowRunPersistenceRecord run,
        Func<WorkerOptions, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        await this.ExecuteInTransaction(
            async (transaction, transactionOptions, transactionCancellationToken) =>
            {
                await this.UpsertRun(run, transaction, transactionCancellationToken);
                await dispatch(transactionOptions, transactionCancellationToken);
            },
            cancellationToken);
    }

    public async Task CompleteAndDeleteRun(
        WorkflowRunPersistenceRecord run,
        CancellationToken cancellationToken)
    {
        await this.ExecuteInTransaction(
            async (transaction, _, transactionCancellationToken) =>
            {
                await this.UpsertRun(run, transaction, transactionCancellationToken);
                await this.DeleteRun(run.RunId, transaction, transactionCancellationToken);
            },
            cancellationToken);
    }

    private async Task UpsertRun(
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

    private async Task DeleteRun(
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

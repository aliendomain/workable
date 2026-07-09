namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpWorkerAdapterShould
{
    [Fact]
    public async Task ExecuteWorkerActionWithRequestedRevision()
    {
        var workerId = WorkerId.New();
        var operations = new RecordingWorkerOperations();
        var session = new RecordingSession(operations);
        var adapter = new WorkableHttpWorkerAdapter();

        await adapter.Execute(session, workerId, WorkAction.Pause, new WorkableHttpWorkerActionRequest(42));

        Assert.Equal(new WorkerVersion(workerId, 42), operations.ExecutedWorker);
        Assert.Equal(WorkAction.Pause, operations.ExecutedAction);
    }

    [Fact]
    public async Task ExecuteBulkActionWithRequestedFilter()
    {
        var operations = new RecordingWorkerOperations();
        var session = new RecordingSession(operations);
        var adapter = new WorkableHttpWorkerAdapter();

        await adapter.ExecuteAll(
            session,
            WorkAction.Cancel,
            new WorkableHttpWorkerBulkActionRequest("Operations", IncludeSubcategories: false));

        Assert.Equal(WorkAction.Cancel, operations.ExecutedBulkAction);
        Assert.Equal(new WorkerBulkActionFilter("Operations", IncludeSubcategories: false), operations.ExecutedBulkFilter);
    }

    [Fact]
    public async Task ExecuteBulkActionWithoutFilterWhenRequestIsMissing()
    {
        var operations = new RecordingWorkerOperations();
        var session = new RecordingSession(operations);
        var adapter = new WorkableHttpWorkerAdapter();

        await adapter.ExecuteAll(session, WorkAction.Push, null);

        Assert.Equal(WorkAction.Push, operations.ExecutedBulkAction);
        Assert.Null(operations.ExecutedBulkFilter);
    }

    [Fact]
    public async Task ReconfigureWorkerWithRequestedRevisionAndChanges()
    {
        var workerId = WorkerId.New();
        var changes = new WorkerReconfiguration(
            ProfilingEnabled: true,
            Start: WorkStartConfiguration.DoNotStart);
        var operations = new RecordingWorkerOperations();
        var session = new RecordingSession(operations);
        var adapter = new WorkableHttpWorkerAdapter();

        await adapter.Reconfigure(session, workerId, new WorkableHttpWorkerReconfigurationRequest(7, changes));

        Assert.Equal(new WorkerVersion(workerId, 7), operations.ReconfiguredWorker);
        Assert.Same(changes, operations.Reconfiguration);
    }

    [Fact]
    public async Task RejectNullInputs()
    {
        var adapter = new WorkableHttpWorkerAdapter();
        var session = new RecordingSession(new RecordingWorkerOperations());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            adapter.Execute(null!, WorkerId.New(), WorkAction.Start, new WorkableHttpWorkerActionRequest(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            adapter.Execute(session, WorkerId.New(), WorkAction.Start, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            adapter.ExecuteAll(null!, WorkAction.Start, null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            adapter.Reconfigure(
                null!,
                WorkerId.New(),
                new WorkableHttpWorkerReconfigurationRequest(1, new WorkerReconfiguration())));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            adapter.Reconfigure(session, WorkerId.New(), null!));
    }

    private sealed class RecordingSession(IWorkerOperations workers) : IWorkSystemSession
    {
        public string? SystemName => throw new NotSupportedException();

        public WorkSystemState SystemState => throw new NotSupportedException();

        public WorkSystemCapabilities Capabilities => WorkSystemCapabilities.None;

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue => throw new NotSupportedException();

        public IWorkerOperations Workers { get; } = workers;

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();
    }

    private sealed class RecordingWorkerOperations : IWorkerOperations
    {
        public WorkerVersion? ExecutedWorker { get; private set; }

        public WorkAction? ExecutedAction { get; private set; }

        public WorkAction? ExecutedBulkAction { get; private set; }

        public WorkerBulkActionFilter? ExecutedBulkFilter { get; private set; }

        public WorkerVersion? ReconfiguredWorker { get; private set; }

        public WorkerReconfiguration? Reconfiguration { get; private set; }

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkAction action,
            CancellationToken cancellationToken = default)
        {
            this.ExecutedWorker = worker;
            this.ExecutedAction = action;

            return Task.FromResult(WorkActionOutcome.NotFound(action, worker.WorkerId));
        }

        public Task<WorkerBulkActionOutcome> ExecuteAll(
            WorkAction action,
            WorkerBulkActionFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            this.ExecutedBulkAction = action;
            this.ExecutedBulkFilter = filter;

            return Task.FromResult(new WorkerBulkActionOutcome(action, filter ?? WorkerBulkActionFilter.All, 0, []));
        }

        public Task<WorkActionOutcome> Reconfigure(
            WorkerVersion worker,
            WorkerReconfiguration changes,
            CancellationToken cancellationToken = default)
        {
            this.ReconfiguredWorker = worker;
            this.Reconfiguration = changes;

            return Task.FromResult(WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId));
        }
    }
}

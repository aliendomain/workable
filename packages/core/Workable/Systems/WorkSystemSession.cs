namespace Workable;

internal sealed class WorkSystemSession(
    string? systemName,
    WorkRequestContext requestContext,
    WorkSystemCapabilities capabilities,
    Func<WorkSystemState> getSystemState,
    Func<WorkerSnapshot, WorkerReconfiguration, bool> canReconfigureWorker,
    IWorkSystemDiagnostics diagnostics,
    IWorkDiscoveryCatalog discovery,
    IWorkCatalog catalog,
    IWorkQueueService queue,
    IWorkerOperations workers,
    IWorkQueryService query,
    IWorkEventStream events,
    IWorkIterationStatusStream iterationStatuses,
    IWorkChangeStream changes) :
    IWorkSystemSession,
    IWorkSystemCapabilitySource,
    IWorkWorkerReconfigurationAuthorizationSource
{
    public string? SystemName { get; } = systemName;

    internal WorkRequestContext RequestContext { get; } = requestContext;

    public WorkSystemCapabilities Capabilities { get; } = capabilities;

    public WorkSystemState SystemState => getSystemState();

    public IWorkSystemDiagnostics Diagnostics { get; } = diagnostics;

    public IWorkDiscoveryCatalog Discovery { get; } = discovery;

    public IWorkCatalog Catalog { get; } = catalog;

    public Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinition(
        string name,
        long revision,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(changes);

        return this.Catalog is AuthorizedWorkCatalog authorized
            ? authorized.Reconfigure(name, revision, changes, cancellationToken)
            : this.Catalog.TryGet(name, out var definition)
                ? this.Catalog.Reconfigure(
                    new WorkDefinitionVersion(definition.Id, revision),
                    changes,
                    cancellationToken)
                : Task.FromResult(WorkDefinitionReconfigurationOutcome.NotFound(name));
    }

    public IWorkQueueService Queue { get; } = queue;

    public IWorkerOperations Workers { get; } = workers;

    public IWorkQueryService Query { get; } = query;

    public IWorkEventStream Events { get; } = events;

    public IWorkIterationStatusStream IterationStatuses { get; } = iterationStatuses;

    public IWorkChangeStream Changes { get; } = changes;

    bool IWorkWorkerReconfigurationAuthorizationSource.CanReconfigureWorker(
        WorkerSnapshot worker,
        WorkerReconfiguration changes)
        => canReconfigureWorker(worker, changes);
}

namespace Workable;

internal sealed class WorkSystemSession(
    string? systemName,
    Func<WorkSystemState> getSystemState,
    IWorkSystemDiagnostics diagnostics,
    IWorkCatalog catalog,
    IWorkQueueService queue,
    IWorkerOperations workers,
    IWorkQueryService query,
    IWorkEventStream events,
    IWorkChangeStream changes) : IWorkSystemSession
{
    public string? SystemName { get; } = systemName;

    public WorkSystemState SystemState => getSystemState();

    public IWorkSystemDiagnostics Diagnostics { get; } = diagnostics;

    public IWorkCatalog Catalog { get; } = catalog;

    public IWorkQueueService Queue { get; } = queue;

    public IWorkerOperations Workers { get; } = workers;

    public IWorkQueryService Query { get; } = query;

    public IWorkEventStream Events { get; } = events;

    public IWorkChangeStream Changes { get; } = changes;
}

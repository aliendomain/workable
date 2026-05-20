namespace Workable;

internal sealed class WorkSystemSession(
    string? systemName,
    Func<WorkSystemState> getSystemState,
    IWorkSystemDiagnostics diagnostics,
    IWorkCatalog definitionLookupCatalog,
    IWorkCatalog catalog,
    IWorkQueueService queue,
    IWorkerOperations workers,
    IWorkQueryService query,
    IWorkEventStream events) : IWorkSystemSession
{
    public string? SystemName { get; } = systemName;

    public WorkSystemState SystemState => getSystemState();

    public IWorkSystemDiagnostics Diagnostics { get; } = diagnostics;

    public IWorkCatalog Catalog { get; } = catalog;

    public IWorkQueueService Queue { get; } = queue;

    public IWorkerOperations Workers { get; } = workers;

    public IWorkQueryService Query { get; } = query;

    public IWorkEventStream Events { get; } = events;

    public bool TryGetDefinition(
        WorkDefinitionId definitionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
        => definitionLookupCatalog.TryGet(definitionId, out definition);

    public bool TryGetDefinition(
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
        => definitionLookupCatalog.TryGet(name, out definition);
}

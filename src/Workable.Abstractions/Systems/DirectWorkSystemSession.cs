namespace Workable;

public sealed class DirectWorkSystemSession(IWorkSystem system) : IWorkSystemSession
{
    public string? SystemName { get; } = system.Name;

    public WorkSystemState SystemState => system.State;

    public IWorkSystemDiagnostics Diagnostics { get; } = system.Diagnostics;

    public IWorkCatalog Catalog { get; } = system.Catalog;

    public IWorkQueueService Queue { get; } = system.Queue;

    public IWorkerOperations Workers { get; } = system.Workers;

    public IWorkQueryService Query { get; } = system.Query;

    public IWorkEventStream Events { get; } = system.Events;

    public bool TryGetDefinition(
        WorkDefinitionId definitionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
        => this.Catalog.TryGet(definitionId, out definition);

    public bool TryGetDefinition(
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
        => this.Catalog.TryGet(name, out definition);
}

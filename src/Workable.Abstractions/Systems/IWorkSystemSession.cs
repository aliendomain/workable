namespace Workable;

public interface IWorkSystemSession
{
    string? SystemName { get; }

    WorkSystemState SystemState { get; }

    IWorkSystemDiagnostics Diagnostics { get; }

    IWorkCatalog Catalog { get; }

    IWorkQueueService Queue { get; }

    IWorkerOperations Workers { get; }

    IWorkQueryService Query { get; }

    IWorkEventStream Events { get; }

    bool TryGetDefinition(
        WorkDefinitionId definitionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition);

    bool TryGetDefinition(
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition);
}

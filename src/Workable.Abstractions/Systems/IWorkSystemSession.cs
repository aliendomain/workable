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
}

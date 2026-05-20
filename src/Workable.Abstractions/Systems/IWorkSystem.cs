using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkSystem : IAsyncDisposable
{
    WorkSystemId Id { get; }

    string? Name { get; }

    bool RequiresAuthorization { get; }

    WorkSystemState State { get; }

    IWorkCatalog Catalog { get; }

    IWorkQueueService Queue { get; }

    IWorkerOperations Workers { get; }

    IWorkQueryService Query { get; }

    IWorkEventStream Events { get; }

    IWorkSystemDiagnostics Diagnostics { get; }

    IWorkSystemSession CreateSession(WorkActor actor);

    Task Start(CancellationToken cancellationToken = default);

    Task<WorkSystemStopResult> Stop(CancellationToken cancellationToken = default);
}

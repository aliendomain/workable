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

    WorkSystemAccessSummary DescribeAccess(WorkRequestContext requestContext);

    IWorkSystemSession CreateSession(WorkRequestContext requestContext);

    Task Start(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);

    Task<WorkSystemStopResult> Stop(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);
}

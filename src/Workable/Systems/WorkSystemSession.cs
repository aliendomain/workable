namespace Workable;

internal sealed class WorkSystemSession(
    IWorkCatalog catalog,
    IWorkQueueService queue,
    IWorkerOperations workers,
    IWorkQueryService query,
    IWorkEventStream events) : IWorkSystemSession
{
    public IWorkCatalog Catalog { get; } = catalog;

    public IWorkQueueService Queue { get; } = queue;

    public IWorkerOperations Workers { get; } = workers;

    public IWorkQueryService Query { get; } = query;

    public IWorkEventStream Events { get; } = events;
}

namespace Workable;

public interface IWorkSystemSession
{
    IWorkCatalog Catalog { get; }

    IWorkQueueService Queue { get; }

    IWorkerOperations Workers { get; }

    IWorkQueryService Query { get; }

    IWorkEventStream Events { get; }
}

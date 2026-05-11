using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkSystem : IAsyncDisposable
{
    WorkSystemId Id { get; }

    string? Name { get; }

    WorkSystemState State { get; }

    IWorkCatalog Catalog { get; }

    IWorkQueue Queue { get; }

    IWorkerOperations Workers { get; }

    IWorkQuery Query { get; }

    IWorkEventStream Events { get; }

    Task Start(CancellationToken cancellationToken = default);

    Task Stop(CancellationToken cancellationToken = default);
}

using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkerOperations
{
    Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default);

    Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default);

    Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default);
}

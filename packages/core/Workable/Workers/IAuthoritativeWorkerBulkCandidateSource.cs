namespace Workable;

internal interface IAuthoritativeWorkerBulkCandidateSource
{
    IReadOnlyList<WorkerSnapshot> GetBulkActionCandidates(
        WorkerBulkActionFilter filter,
        IReadOnlySet<WorkDefinitionId>? definitionIds,
        CancellationToken cancellationToken);
}

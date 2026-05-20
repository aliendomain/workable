namespace Workable;

internal sealed class AuthorizedWorkerOperations(
    IWorkerOperations inner,
    IWorkQueryService query,
    WorkAuthorizationScope scope) : IWorkerOperations
{
    public async Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default)
    {
        if (!await this.CanOperate(worker.WorkerId, cancellationToken))
        {
            return WorkActionOutcome.NotFound(action, worker.WorkerId);
        }

        return await inner.Execute(worker, action, cancellationToken);
    }

    public async Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= WorkerBulkActionFilter.All;
        var outcomes = new List<WorkActionOutcome>();
        var skip = 0;

        while (true)
        {
            var criteria = new WorkerCriteria(
                Category: filter.Category,
                IncludeSubcategories: filter.IncludeSubcategories,
                Skip: skip,
                Take: WorkerCriteria.MaximumTake);
            var result = await query.Workers(criteria, cancellationToken);
            if (result.Workers.Count == 0)
            {
                break;
            }

            foreach (var worker in result.Workers.Where(worker => scope.CanOperate(worker.DefinitionId)))
            {
                outcomes.Add(await inner.Execute(new WorkerVersion(worker.Id, worker.Revision), action, cancellationToken));
            }

            if (result.Workers.Count < WorkerCriteria.MaximumTake)
            {
                break;
            }

            skip += result.Workers.Count;
        }

        return new WorkerBulkActionOutcome(action, filter, outcomes.Count, outcomes);
    }

    public async Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        if (!await this.CanOperate(worker.WorkerId, cancellationToken))
        {
            return WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId);
        }

        return await inner.Reconfigure(worker, changes, cancellationToken);
    }

    private async Task<bool> CanOperate(WorkerId workerId, CancellationToken cancellationToken)
    {
        var worker = await query.Worker(workerId, cancellationToken);
        return worker is not null && scope.CanOperate(worker.DefinitionId);
    }
}

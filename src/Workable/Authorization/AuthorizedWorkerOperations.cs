namespace Workable;

internal sealed class AuthorizedWorkerOperations(
    IWorkCatalog catalog,
    IWorkerOperations inner,
    IWorkQueryService query,
    WorkAuthorizationEvaluator authorization) : IWorkerOperations
{
    public async Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default)
    {
        var authorizationResult = await this.AuthorizeWorker(worker.WorkerId, cancellationToken);
        if (authorizationResult is WorkerAuthorizationResult.NotFound)
        {
            return WorkActionOutcome.NotFound(action, worker.WorkerId);
        }

        if (authorizationResult is WorkerAuthorizationResult.Unauthorized)
        {
            return WorkActionOutcome.Unauthorized(action, worker.WorkerId);
        }

        return await inner.Execute(worker, action, cancellationToken);
    }

    public async Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= WorkerBulkActionFilter.All;
        var definitionNames = authorization.HasOperateAllWorkAccess()
            ? null
            : authorization.OperableDefinitions()
                .Select(definition => definition.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (definitionNames is { Count: 0 })
        {
            return new WorkerBulkActionOutcome(action, filter, 0, []);
        }

        var outcomes = new List<WorkActionOutcome>();
        var skip = 0;

        while (true)
        {
            var criteria = new WorkerCriteria(
                Category: filter.Category,
                IncludeSubcategories: filter.IncludeSubcategories,
                Skip: skip,
                Take: WorkerCriteria.MaximumTake,
                DefinitionNames: definitionNames);
            var result = await query.Workers(criteria, cancellationToken);
            if (result.Workers.Count == 0)
            {
                break;
            }

            foreach (var worker in result.Workers)
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
        var authorizationResult = await this.AuthorizeWorker(worker.WorkerId, cancellationToken);
        if (authorizationResult is WorkerAuthorizationResult.NotFound)
        {
            return WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId);
        }

        if (authorizationResult is WorkerAuthorizationResult.Unauthorized)
        {
            return WorkActionOutcome.Unauthorized(WorkAction.Start, worker.WorkerId);
        }

        return await inner.Reconfigure(worker, changes, cancellationToken);
    }

    private async Task<WorkerAuthorizationResult> AuthorizeWorker(WorkerId workerId, CancellationToken cancellationToken)
    {
        var worker = await query.Worker(workerId, cancellationToken);
        if (worker is null)
        {
            return WorkerAuthorizationResult.NotFound;
        }

        return catalog.TryGet(worker.DefinitionName, out var definition) &&
            authorization.CanOperate(definition)
            ? WorkerAuthorizationResult.Authorized
            : WorkerAuthorizationResult.Unauthorized;
    }

    private enum WorkerAuthorizationResult
    {
        Authorized,
        Unauthorized,
        NotFound,
    }
}

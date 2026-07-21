namespace Workable;

internal sealed class AuthorizedWorkerOperations(
    WorkSystemCatalog catalog,
    IWorkerOperations inner,
    IWorkQueryService query,
    WorkAuthorizationEvaluator authorization,
    WorkRequestContext requestContext) : IWorkerOperations
{
    public Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkAction action,
        CancellationToken cancellationToken = default)
        => this.Execute(worker, new WorkerActionRequest(action), cancellationToken);

    public async Task<WorkActionOutcome> Execute(
        WorkerVersion worker,
        WorkerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = request.Action;
        var authorizationResult = await this.AuthorizeAction(worker.WorkerId, action, cancellationToken);
        if (authorizationResult.Status is WorkerActionAuthorizationStatus.NotFound)
        {
            return WorkActionOutcome.NotFound(action, worker.WorkerId);
        }

        if (authorizationResult.Status is WorkerActionAuthorizationStatus.Unauthorized)
        {
            return WorkActionOutcome.Unauthorized(action, worker.WorkerId);
        }

        if (authorizationResult.Status is WorkerActionAuthorizationStatus.Invalid)
        {
            return WorkActionOutcome.Invalid(
                action,
                authorizationResult.Worker,
                authorizationResult.Messages);
        }

        return await inner.Execute(worker, request, cancellationToken);
    }

    public async Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= WorkerBulkActionFilter.All;
        var definitionNames = authorization.HasSystemOperateAllWorkAccess()
            ? null
            : authorization.OperableDefinitionNamesFor(action);
        if (definitionNames is { Count: 0 })
        {
            return new WorkerBulkActionOutcome(action, filter, 0, []);
        }

        var outcomes = new List<WorkActionOutcome>();
        var matchedWorkerCount = 0;
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
                matchedWorkerCount++;
                var authorizationResult = await this.AuthorizeAction(worker.Id, action, cancellationToken);
                outcomes.Add(authorizationResult.Status switch
                {
                    WorkerActionAuthorizationStatus.Authorized => await inner.Execute(
                        new WorkerVersion(worker.Id, worker.Revision),
                        action,
                        cancellationToken),
                    WorkerActionAuthorizationStatus.Invalid => WorkActionOutcome.Invalid(
                        action,
                        authorizationResult.Worker,
                        authorizationResult.Messages),
                    WorkerActionAuthorizationStatus.NotFound => WorkActionOutcome.NotFound(action, worker.Id),
                    _ => WorkActionOutcome.Unauthorized(action, worker.Id),
                });
            }

            if (result.Workers.Count < WorkerCriteria.MaximumTake)
            {
                break;
            }

            skip += result.Workers.Count;
        }

        return new WorkerBulkActionOutcome(action, filter, matchedWorkerCount, outcomes);
    }

    public async Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        var authorizationResult = await this.AuthorizeReconfiguration(worker.WorkerId, changes, cancellationToken);
        if (authorizationResult.Status is WorkerActionAuthorizationStatus.NotFound)
        {
            return WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId);
        }

        if (authorizationResult.Status is WorkerActionAuthorizationStatus.Unauthorized)
        {
            return WorkActionOutcome.Unauthorized(WorkAction.Start, worker.WorkerId);
        }

        if (authorizationResult.Status is WorkerActionAuthorizationStatus.Invalid)
        {
            return WorkActionOutcome.Invalid(
                WorkAction.Start,
                authorizationResult.Worker,
                authorizationResult.Messages);
        }

        return await inner.Reconfigure(worker, changes, cancellationToken);
    }

    private async Task<WorkerActionAuthorizationResult> AuthorizeAction(
        WorkerId workerId,
        WorkAction action,
        CancellationToken cancellationToken)
    {
        var worker = await query.Worker(workerId, cancellationToken);
        if (worker is null)
        {
            return WorkerActionAuthorizationResult.NotFound();
        }

        if (!catalog.TryGetWork(worker.DefinitionName, out var registeredWork))
        {
            return WorkerActionAuthorizationResult.Unauthorized(worker);
        }

        var decision = authorization.AuthorizeWorkerAction(
            registeredWork,
            worker,
            action,
            requestContext);
        if (decision.IsAllowed)
        {
            return WorkerActionAuthorizationResult.Authorized(worker);
        }

        return decision.IsInvalid
            ? WorkerActionAuthorizationResult.Invalid(worker, decision.Messages)
            : WorkerActionAuthorizationResult.Unauthorized(worker);
    }

    private async Task<WorkerActionAuthorizationResult> AuthorizeReconfiguration(
        WorkerId workerId,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken)
    {
        var worker = await query.Worker(workerId, cancellationToken);
        if (worker is null)
        {
            return WorkerActionAuthorizationResult.NotFound();
        }

        if (!catalog.TryGetWork(worker.DefinitionName, out var registeredWork))
        {
            return WorkerActionAuthorizationResult.Unauthorized(worker);
        }

        var decision = authorization.AuthorizeWorkerReconfiguration(
            registeredWork,
            worker,
            changes,
            requestContext);
        if (decision.IsAllowed)
        {
            return WorkerActionAuthorizationResult.Authorized(worker);
        }

        return decision.IsInvalid
            ? WorkerActionAuthorizationResult.Invalid(worker, decision.Messages)
            : WorkerActionAuthorizationResult.Unauthorized(worker);
    }

    private enum WorkerActionAuthorizationStatus
    {
        Authorized,
        Unauthorized,
        Invalid,
        NotFound,
    }

    private sealed record WorkerActionAuthorizationResult(
        WorkerActionAuthorizationStatus Status,
        WorkerSnapshot? Worker,
        IReadOnlyList<WorkMessage> Messages)
    {
        public static WorkerActionAuthorizationResult Authorized(WorkerSnapshot worker)
            => new(WorkerActionAuthorizationStatus.Authorized, worker, []);

        public static WorkerActionAuthorizationResult Unauthorized(WorkerSnapshot worker)
            => new(WorkerActionAuthorizationStatus.Unauthorized, worker, []);

        public static WorkerActionAuthorizationResult Invalid(
            WorkerSnapshot worker,
            IReadOnlyList<WorkMessage> messages)
            => new(WorkerActionAuthorizationStatus.Invalid, worker, messages);

        public static WorkerActionAuthorizationResult NotFound()
            => new(WorkerActionAuthorizationStatus.NotFound, null, []);
    }
}

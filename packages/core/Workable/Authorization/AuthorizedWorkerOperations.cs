namespace Workable;

internal sealed class AuthorizedWorkerOperations(
    WorkSystemCatalog catalog,
    IWorkerOperations inner,
    IAuthoritativeWorkerBulkCandidateSource bulkCandidates,
    IWorkQueryService query,
    WorkAuthorizationEvaluator authorization,
    WorkRequestContext requestContext,
    bool canViewDiagnostics) : IWorkerOperations
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
            return this.FilterOutcome(WorkActionOutcome.Invalid(
                action,
                authorizationResult.Worker,
                authorizationResult.Messages));
        }

        return this.FilterOutcome(await inner.Execute(worker, request, cancellationToken));
    }

    public async Task<WorkerBulkActionOutcome> ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= WorkerBulkActionFilter.All;
        var definitionIds = authorization.HasSystemOperateAllWorkAccess()
            ? null
            : authorization.OperableDefinitionIdsFor(action);
        if (definitionIds is { Count: 0 })
        {
            return new WorkerBulkActionOutcome(action, filter, 0, []);
        }

        var candidates = bulkCandidates.GetBulkActionCandidates(
            filter,
            definitionIds,
            cancellationToken);
        var outcomes = new List<WorkActionOutcome>(candidates.Count);
        foreach (var worker in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorizationResult = this.AuthorizeAction(worker, action);
            if (authorizationResult.Status is not WorkerActionAuthorizationStatus.Authorized)
            {
                continue;
            }

            outcomes.Add(this.FilterOutcome(await inner.Execute(
                worker.Version,
                action,
                cancellationToken)));
        }

        return new WorkerBulkActionOutcome(action, filter, outcomes.Count, outcomes);
    }

    public async Task<WorkActionOutcome> Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
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
            return this.FilterOutcome(WorkActionOutcome.Invalid(
                WorkAction.Start,
                authorizationResult.Worker,
                authorizationResult.Messages));
        }

        return this.FilterOutcome(await inner.Reconfigure(worker, changes, cancellationToken));
    }

    private WorkActionOutcome FilterOutcome(WorkActionOutcome outcome)
    {
        if (outcome.Worker is { } worker &&
            (!catalog.TryGetWork(worker.DefinitionName, out var registeredWork) ||
                !authorization.CanRead(registeredWork.Definition)))
        {
            return outcome with { Worker = null };
        }

        return WorkProfileAccessFilter.Apply(outcome, canViewDiagnostics);
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

        return this.AuthorizeAction(worker, action);
    }

    private WorkerActionAuthorizationResult AuthorizeAction(
        WorkerSnapshot worker,
        WorkAction action)
    {
        if (!catalog.TryGetWork(worker.DefinitionName, out var registeredWork))
        {
            return WorkerActionAuthorizationResult.NotFound();
        }

        if (!authorization.CanDiscover(registeredWork.Definition))
        {
            return WorkerActionAuthorizationResult.NotFound();
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
            return WorkerActionAuthorizationResult.NotFound();
        }

        if (!authorization.CanDiscover(registeredWork.Definition))
        {
            return WorkerActionAuthorizationResult.NotFound();
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

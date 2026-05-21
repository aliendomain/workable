namespace Workable;

internal sealed class AuthorizedWorkQueryService(
    IWorkCatalog catalog,
    IWorkQueryService inner,
    WorkAuthorizationEvaluator authorization) : IWorkQueryService
{
    public IWorkQueryService BeginRead()
        => new AuthorizedWorkQueryService(catalog, inner.BeginRead(), authorization);

    public async Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        var worker = await inner.Worker(workerId, cancellationToken);
        return worker is not null && authorization.CanRead(worker.DefinitionId) ? worker : null;
    }

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        var worker = await this.Worker(iteration.WorkerId, cancellationToken);
        return worker is null ? null : await inner.WorkerIteration(iteration, cancellationToken);
    }

    public async Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkerCriteria();
        var skip = Math.Max(0, query.Skip);
        var take = NormalizeTake(query.Take, WorkerCriteria.DefaultTake, WorkerCriteria.MaximumTake);
        var authorized = this.AuthorizeWorkerCriteria(query);
        return authorized is null
            ? new WorkerQueryResult([], 0, skip, take)
            : await inner.Workers(authorized with { Skip = skip, Take = take }, cancellationToken);
    }

    public async Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkerIterationCriteria();
        var skip = Math.Max(0, query.Skip);
        var take = NormalizeTake(query.Take, WorkerIterationCriteria.DefaultTake, WorkerIterationCriteria.MaximumTake);
        var authorized = this.AuthorizeIterationCriteria(query);
        return authorized is null
            ? new WorkerIterationQueryResult([], 0, skip, take)
            : await inner.WorkerIterations(authorized with { Skip = skip, Take = take }, cancellationToken);
    }

    public Task<WorkInfo?> WorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
        => authorization.CanRead(definitionId)
            ? inner.WorkInfo(definitionId, cancellationToken)
            : Task.FromResult<WorkInfo?>(null);

    public async Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(name, out var definition) || !authorization.CanRead(definition))
        {
            return null;
        }

        return await inner.WorkInfo(name, cancellationToken);
    }

    public async Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var authorized = this.AuthorizeDefinitionCriteria(criteria ?? new WorkDefinitionCriteria());
        return authorized is null
            ? new WorkDefinitionQueryResult([])
            : await inner.WorkDefinitions(authorized, cancellationToken);
    }

    public async Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkerKeyCriteria();
        var authorized = this.AuthorizeWorkerKeyCriteria(query);
        return authorized is null
            ? new WorkerKeyQueryResult([], 0, Math.Max(0, query.Skip), NormalizeTake(query.Take, WorkerKeyCriteria.DefaultTake, WorkerKeyCriteria.MaximumTake))
            : await inner.WorkerKeys(authorized, cancellationToken);
    }

    public async Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkerKeyTypeCriteria();
        var authorized = this.AuthorizeWorkerKeyTypeCriteria(query);
        return authorized is null
            ? new WorkerKeyTypeQueryResult([], 0, Math.Max(0, query.Skip), NormalizeTake(query.Take, WorkerKeyCriteria.DefaultTake, WorkerKeyCriteria.MaximumTake))
            : await inner.WorkerKeyTypes(authorized, cancellationToken);
    }

    public async Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkIterationKeyCriteria();
        var authorized = this.AuthorizeIterationKeyCriteria(query);
        return authorized is null
            ? new WorkIterationKeyQueryResult([], 0, Math.Max(0, query.Skip), NormalizeTake(query.Take, WorkIterationKeyCriteria.DefaultTake, WorkIterationKeyCriteria.MaximumTake))
            : await inner.WorkIterationKeys(authorized, cancellationToken);
    }

    public async Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkIterationKeyTypeCriteria();
        var authorized = this.AuthorizeIterationKeyTypeCriteria(query);
        return authorized is null
            ? new WorkIterationKeyTypeQueryResult([], 0, Math.Max(0, query.Skip), NormalizeTake(query.Take, WorkIterationKeyCriteria.DefaultTake, WorkIterationKeyCriteria.MaximumTake))
            : await inner.WorkIterationKeyTypes(authorized, cancellationToken);
    }

    public async Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var authorized = this.AuthorizeWorkerCriteria(criteria ?? new WorkerCriteria());
        return authorized is null
            ? new WorkerStatusSummary(0, 0, 0, new Dictionary<WorkerState, int>())
            : await inner.WorkerStatusSummary(authorized, cancellationToken);
    }

    public async Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        return await inner.SystemDetails(this.AuthorizeSystemCriteria(criteria), cancellationToken);
    }

    public Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
        => inner.SystemThroughput(this.AuthorizeSystemCriteria(criteria), throughput, cancellationToken);

    public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
        => inner.SystemThroughputSummary(this.AuthorizeSystemCriteria(criteria), throughput, cancellationToken);

    public async Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => await inner.SystemWorkerCounts(this.AuthorizeSystemCriteria(criteria), cancellationToken);

    public async Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => await inner.SystemIterationCounts(this.AuthorizeSystemCriteria(criteria), cancellationToken);

    public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => inner.SystemCommonKeyTypes(this.AuthorizeSystemCriteria(criteria), cancellationToken);

    public async Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => await inner.SystemFailedWorkers(this.AuthorizeSystemCriteria(criteria), cancellationToken);

    public async Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => await inner.SystemFailedIterations(this.AuthorizeSystemCriteria(criteria), cancellationToken);

    public async Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => await inner.SystemCompletedIterations(this.AuthorizeSystemCriteria(criteria), cancellationToken);

    private WorkDefinitionCriteria? AuthorizeDefinitionCriteria(WorkDefinitionCriteria criteria)
        => ApplyDefinitionScope(
            criteria,
            this.ResolveReadableDefinitionScope(criteria.Id, criteria.Name, criteria.DefinitionIds));

    private WorkerCriteria? AuthorizeWorkerCriteria(WorkerCriteria criteria)
        => ApplyDefinitionScope(
            criteria,
            this.ResolveReadableDefinitionScope(criteria.DefinitionId, criteria.DefinitionName, criteria.DefinitionIds));

    private WorkerIterationCriteria? AuthorizeIterationCriteria(WorkerIterationCriteria criteria)
        => ApplyDefinitionScope(
            criteria,
            this.ResolveReadableDefinitionScope(criteria.DefinitionId, criteria.DefinitionName, criteria.DefinitionIds));

    private WorkerKeyCriteria? AuthorizeWorkerKeyCriteria(WorkerKeyCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionIds));

    private WorkerKeyTypeCriteria? AuthorizeWorkerKeyTypeCriteria(WorkerKeyTypeCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionIds));

    private WorkIterationKeyCriteria? AuthorizeIterationKeyCriteria(WorkIterationKeyCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionIds));

    private WorkIterationKeyTypeCriteria? AuthorizeIterationKeyTypeCriteria(WorkIterationKeyTypeCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionIds));

    private WorkSystemCriteria? AuthorizeSystemCriteria(WorkSystemCriteria? criteria)
    {
        var query = criteria ?? new WorkSystemCriteria();
        var definitionIds = this.ResolveReadableDefinitionScope(
            query.DefinitionId,
            query.DefinitionName,
            query.DefinitionIds);

        return definitionIds is null
            ? criteria
            : query with { DefinitionIds = definitionIds };
    }

    private IReadOnlySet<WorkDefinitionId>? ResolveReadableDefinitionScope(
        IReadOnlySet<WorkDefinitionId>? requestedDefinitionIds)
        => this.ResolveReadableDefinitionScope(null, null, requestedDefinitionIds);

    private IReadOnlySet<WorkDefinitionId>? ResolveReadableDefinitionScope(
        WorkDefinitionId? definitionId,
        string? definitionName,
        IReadOnlySet<WorkDefinitionId>? requestedDefinitionIds)
    {
        var hasAllDefinitions = authorization.HasReadAllWorkAccess();
        return this.ResolveDefinitionScope(
            definitionId,
            definitionName,
            requestedDefinitionIds,
            hasAllDefinitions,
            hasAllDefinitions ? new HashSet<WorkDefinitionId>() : authorization.ReadableDefinitionIds());
    }

    private IReadOnlySet<WorkDefinitionId>? ResolveDefinitionScope(
        WorkDefinitionId? definitionId,
        string? definitionName,
        IReadOnlySet<WorkDefinitionId>? requestedDefinitionIds,
        bool hasAllDefinitions,
        IReadOnlySet<WorkDefinitionId> allowedDefinitionIds)
    {
        HashSet<WorkDefinitionId>? definitionIds = requestedDefinitionIds?.ToHashSet();
        if (definitionId is { } id)
        {
            definitionIds = IntersectDefinitionScope(definitionIds, [id]);
        }

        if (!string.IsNullOrWhiteSpace(definitionName))
        {
            if (!catalog.TryGet(definitionName, out var definition))
            {
                return new HashSet<WorkDefinitionId>();
            }

            definitionIds = IntersectDefinitionScope(definitionIds, [definition.Id]);
        }

        return hasAllDefinitions
            ? definitionIds
            : IntersectDefinitionScope(definitionIds, allowedDefinitionIds);
    }

    private static WorkDefinitionCriteria? ApplyDefinitionScope(
        WorkDefinitionCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static WorkerCriteria? ApplyDefinitionScope(
        WorkerCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static WorkerIterationCriteria? ApplyDefinitionScope(
        WorkerIterationCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static WorkerKeyCriteria? ApplyDefinitionScope(
        WorkerKeyCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static WorkerKeyTypeCriteria? ApplyDefinitionScope(
        WorkerKeyTypeCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static WorkIterationKeyCriteria? ApplyDefinitionScope(
        WorkIterationKeyCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static WorkIterationKeyTypeCriteria? ApplyDefinitionScope(
        WorkIterationKeyTypeCriteria criteria,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? criteria
            : definitionIds.Count == 0 ? null : criteria with { DefinitionIds = definitionIds };

    private static HashSet<WorkDefinitionId> IntersectDefinitionScope(
        HashSet<WorkDefinitionId>? current,
        IEnumerable<WorkDefinitionId> requested)
    {
        var requestedSet = requested.ToHashSet();
        if (current is null)
        {
            return requestedSet;
        }

        current.IntersectWith(requestedSet);
        return current;
    }

    private static int NormalizeTake(int take, int defaultTake, int maximumTake)
        => take <= 0 ? defaultTake : Math.Min(take, maximumTake);
}

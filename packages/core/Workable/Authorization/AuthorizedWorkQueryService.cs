namespace Workable;

internal sealed class AuthorizedWorkQueryService(
    IWorkCatalog catalog,
    IWorkQueryService inner,
    WorkAuthorizationEvaluator authorization,
    bool canViewDiagnostics) : IWorkQueryService
{
    public async Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        var worker = await inner.Worker(workerId, cancellationToken);
        if (worker is null)
        {
            return null;
        }

        return catalog.TryGet(worker.DefinitionName, out var definition) && authorization.CanRead(definition)
            ? WorkProfileAccessFilter.Apply(worker, canViewDiagnostics)
            : null;
    }

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        var worker = await this.Worker(iteration.WorkerId, cancellationToken);
        var snapshot = worker is null ? null : await inner.WorkerIteration(iteration, cancellationToken);
        return snapshot is null ? null : WorkProfileAccessFilter.Apply(snapshot, canViewDiagnostics);
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
            this.ResolveReadableDefinitionScope(criteria.Name, criteria.Names));

    private WorkerCriteria? AuthorizeWorkerCriteria(WorkerCriteria criteria)
        => ApplyDefinitionScope(
            criteria,
            this.ResolveReadableDefinitionScope(criteria.DefinitionName, criteria.DefinitionNames));

    private WorkerIterationCriteria? AuthorizeIterationCriteria(WorkerIterationCriteria criteria)
        => ApplyDefinitionScope(
            criteria,
            this.ResolveReadableDefinitionScope(criteria.DefinitionName, criteria.DefinitionNames));

    private WorkerKeyCriteria? AuthorizeWorkerKeyCriteria(WorkerKeyCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionNames));

    private WorkerKeyTypeCriteria? AuthorizeWorkerKeyTypeCriteria(WorkerKeyTypeCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionNames));

    private WorkIterationKeyCriteria? AuthorizeIterationKeyCriteria(WorkIterationKeyCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionNames));

    private WorkIterationKeyTypeCriteria? AuthorizeIterationKeyTypeCriteria(WorkIterationKeyTypeCriteria criteria)
        => ApplyDefinitionScope(criteria, this.ResolveReadableDefinitionScope(criteria.DefinitionNames));

    private WorkSystemCriteria? AuthorizeSystemCriteria(WorkSystemCriteria? criteria)
    {
        var query = criteria ?? new WorkSystemCriteria();
        var definitionNames = this.ResolveReadableDefinitionScope(
            query.DefinitionName,
            query.DefinitionNames);

        return definitionNames is null
            ? criteria
            : query with { DefinitionNames = definitionNames };
    }

    private IReadOnlySet<string>? ResolveReadableDefinitionScope(
        IReadOnlySet<string>? requestedDefinitionNames)
        => this.ResolveReadableDefinitionScope(null, requestedDefinitionNames);

    private IReadOnlySet<string>? ResolveReadableDefinitionScope(
        string? definitionName,
        IReadOnlySet<string>? requestedDefinitionNames)
    {
        var hasAllDefinitions = authorization.HasReadAllWorkAccess();
        return this.ResolveDefinitionScope(
            definitionName,
            requestedDefinitionNames,
            hasAllDefinitions,
            hasAllDefinitions
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : catalog.Definitions
                    .Where(definition => authorization.CanRead(definition))
                    .Select(definition => definition.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private IReadOnlySet<string>? ResolveDefinitionScope(
        string? definitionName,
        IReadOnlySet<string>? requestedDefinitionNames,
        bool hasAllDefinitions,
        IReadOnlySet<string> allowedDefinitionNames)
    {
        HashSet<string>? definitionNames = requestedDefinitionNames is null
            ? null
            : requestedDefinitionNames.Count == 0
                ? []
                : requestedDefinitionNames
                    .Select(name => catalog.TryGet(name, out var definition) ? definition?.Name : null)
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(definitionName))
        {
            if (!catalog.TryGet(definitionName, out var definition))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            definitionNames = IntersectDefinitionScope(definitionNames, [definition.Name]);
        }

        return hasAllDefinitions
            ? definitionNames
            : IntersectDefinitionScope(definitionNames, allowedDefinitionNames);
    }

    private static WorkDefinitionCriteria? ApplyDefinitionScope(
        WorkDefinitionCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { Names = definitionNames };

    private static WorkerCriteria? ApplyDefinitionScope(
        WorkerCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { DefinitionNames = definitionNames };

    private static WorkerIterationCriteria? ApplyDefinitionScope(
        WorkerIterationCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { DefinitionNames = definitionNames };

    private static WorkerKeyCriteria? ApplyDefinitionScope(
        WorkerKeyCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { DefinitionNames = definitionNames };

    private static WorkerKeyTypeCriteria? ApplyDefinitionScope(
        WorkerKeyTypeCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { DefinitionNames = definitionNames };

    private static WorkIterationKeyCriteria? ApplyDefinitionScope(
        WorkIterationKeyCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { DefinitionNames = definitionNames };

    private static WorkIterationKeyTypeCriteria? ApplyDefinitionScope(
        WorkIterationKeyTypeCriteria criteria,
        IReadOnlySet<string>? definitionNames)
        => definitionNames is null
            ? criteria
            : definitionNames.Count == 0 ? null : criteria with { DefinitionNames = definitionNames };

    private static HashSet<string> IntersectDefinitionScope(
        HashSet<string>? current,
        IEnumerable<string> requested)
    {
        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
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

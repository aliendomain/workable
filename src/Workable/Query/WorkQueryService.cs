using System.Collections.Concurrent;
namespace Workable;

internal sealed partial class WorkQueryService(
    WorkSystemCatalog catalog,
    Func<WorkSystemState> getSystemState,
    string? workSystemName,
    ConcurrentDictionary<WorkerId, WorkerRecord> workers,
    WorkerIndex index,
    WorkerIterationIndex iterationIndex,
    InMemoryWorkMetricsSink metrics) : IWorkQueryService
{
    private const int SystemWorkerListSize = 5;
    private const int SystemIterationListSize = 5;
    private const int SystemCommonKeyTypeCount = 10;

    private readonly WorkSystemCatalog catalog = catalog;
    private readonly Func<WorkSystemState> getSystemState = getSystemState;
    private readonly string? workSystemName = workSystemName;
    private readonly ConcurrentDictionary<WorkerId, WorkerRecord> workers = workers;
    private readonly WorkerIndex index = index;
    private readonly WorkerIterationIndex iterationIndex = iterationIndex;
    private readonly InMemoryWorkMetricsSink metrics = metrics;

    public Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.workers.TryGetValue(workerId, out var worker) ? worker.ToSnapshot() : null);
    }

    public Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.iterationIndex.Get(iteration));
    }

    public Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkerCriteria();
        var candidates = this.GetCandidateWorkers(query);
        var filtered = candidates
            .Select(worker => new
            {
                Record = worker,
                Overview = worker.ToOverviewItem(),
            })
            .Where(worker => Matches(worker.Overview, query) && Matches(worker.Record, query.Configuration))
            .Select(worker => worker.Overview);

        filtered = Sort(filtered, query.Sort, query.Direction);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = query.Take <= 0
            ? WorkerCriteria.DefaultTake
            : Math.Min(query.Take, WorkerCriteria.MaximumTake);
        var materialized = filtered.ToList();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerQueryResult(page, materialized.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkerIterationCriteria();
        var normalizedQuery = query;
        if (!string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            if (!this.catalog.TryGet(query.DefinitionName, out var definition))
            {
                var emptySkip = Math.Max(0, query.Skip);
                var emptyTake = query.Take <= 0
                    ? WorkerIterationCriteria.DefaultTake
                    : Math.Min(query.Take, WorkerIterationCriteria.MaximumTake);
                return Task.FromResult(new WorkerIterationQueryResult([], 0, emptySkip, emptyTake));
            }

            normalizedQuery = query with
            {
                DefinitionId = definition.Id,
            };
        }

        IReadOnlySet<WorkDefinitionId>? definitionIds = null;
        if (!string.IsNullOrWhiteSpace(normalizedQuery.Category))
        {
            definitionIds = this.catalog
                .ListByCategory(normalizedQuery.Category, includeSubcategories: true)
                .Select(definition => definition.Id)
                .ToHashSet();
        }

        var iterations = this.iterationIndex.Find(normalizedQuery, definitionIds)
            .Where(iteration => Matches(iteration, normalizedQuery))
            .Select(iteration => iteration.ToOverviewItem());

        iterations = Sort(iterations, query.Sort, query.Direction);

        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = query.Take <= 0
            ? WorkerIterationCriteria.DefaultTake
            : Math.Min(query.Take, WorkerIterationCriteria.MaximumTake);
        var materialized = iterations.ToList();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerIterationQueryResult(page, materialized.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkInfo?> WorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!this.catalog.TryGet(definitionId, out var definition))
        {
            return Task.FromResult<WorkInfo?>(null);
        }

        return Task.FromResult<WorkInfo?>(this.CreateWorkInfo(definition));
    }

    public Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!this.catalog.TryGet(name, out var definition))
        {
            return Task.FromResult<WorkInfo?>(null);
        }

        return Task.FromResult<WorkInfo?>(this.CreateWorkInfo(definition));
    }

    public Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkDefinitionCriteria();
        var candidates = string.IsNullOrWhiteSpace(query.Category)
            ? this.catalog.Definitions
            : this.catalog.ListByCategory(query.Category, query.IncludeSubcategories);
        var definitions = candidates
            .Where(definition => Matches(definition, query))
            .OrderBy(definition => definition.Category)
            .ThenBy(definition => definition.Name);
        return Task.FromResult(new WorkDefinitionQueryResult([.. definitions]));
    }

    public Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkerKeyCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkKeyTake(query.Take);
        var matches = this.index.WorkKeys(query.Kind, query.Type, query.Value)
            .Where(key => Matches(key, query))
            .OrderBy(key => key.Kind)
            .ThenBy(key => key.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Value, StringComparer.OrdinalIgnoreCase)
            .Select(key => new WorkerKeyDescriptor(
                key.Kind,
                key.Type,
                key.Value,
                this.GetOverviewItems(key.WorkerIds, query.States)))
            .Where(key => key.Workers.Count > 0)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerKeyQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkerKeyTypeCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkKeyTake(query.Take);
        if (query.States is null)
        {
            var facets = this.index.KeyTypes(query.Kind, query.Type, query.Search);
            var keyTypePage = facets
                .Skip(normalizedSkip)
                .Take(normalizedTake)
                .Select(facet => new WorkerKeyTypeDescriptor(
                    facet.Type,
                    facet.WorkerCount,
                    facet.WorkerCountByKind,
                    this.GetOverviewItems(this.index.WorkerIdsByKeyType(facet.Type, query.Kind))))
                .ToArray();

            return Task.FromResult(new WorkerKeyTypeQueryResult(keyTypePage, facets.Count, normalizedSkip, normalizedTake));
        }

        var matches = this.index.WorkKeys(query.Kind, query.Type, null)
            .Where(key => Matches(key, query))
            .GroupBy(key => key.Type.ToUpperInvariant())
            .Select(group =>
            {
                var first = group.First();
                var workers = this.GetOverviewItems(group.SelectMany(key => key.WorkerIds).Distinct(), query.States);
                return new WorkerKeyTypeDescriptor(
                    first.Type,
                    workers.Count,
                    CountWorkersByKind(group, query.States),
                    workers);
            })
            .Where(keyType => keyType.Workers.Count > 0)
            .OrderByDescending(keyType => keyType.WorkerCount)
            .ThenBy(keyType => keyType.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkerKeyTypeQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkIterationKeyCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkIterationKeyTake(query.Take);
        var matches = this.iterationIndex.WorkKeys(query.Kind, query.Type, query.Value)
            .Where(key => Matches(key, query))
            .OrderBy(key => key.Kind)
            .ThenBy(key => key.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Value, StringComparer.OrdinalIgnoreCase)
            .Select(key => new WorkIterationKeyDescriptor(
                key.Kind,
                key.Type,
                key.Value,
                this.iterationIndex.GetOverviewItems(key.IterationReferences, query.Statuses)))
            .Where(key => key.Iterations.Count > 0)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkIterationKeyQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = criteria ?? new WorkIterationKeyTypeCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkIterationKeyTake(query.Take);
        if (query.Statuses is null)
        {
            var facets = this.iterationIndex.KeyTypes(query.Kind, query.Type, query.Search);
            var keyTypePage = facets
                .Skip(normalizedSkip)
                .Take(normalizedTake)
                .Select(facet => new WorkIterationKeyTypeDescriptor(
                    facet.Type,
                    facet.IterationCount,
                    facet.IterationCountByKind,
                    this.iterationIndex.GetOverviewItems(this.iterationIndex.IterationReferencesByKeyType(facet.Type, query.Kind))))
                .ToArray();

            return Task.FromResult(new WorkIterationKeyTypeQueryResult(keyTypePage, facets.Count, normalizedSkip, normalizedTake));
        }

        var matches = this.iterationIndex.WorkKeys(query.Kind, query.Type, null)
            .Where(key => Matches(key, query))
            .GroupBy(key => key.Type.ToUpperInvariant())
            .Select(group =>
            {
                var first = group.First();
                var iterations = this.iterationIndex.GetOverviewItems(
                    group.SelectMany(key => key.IterationReferences).Distinct(),
                    query.Statuses);
                return new WorkIterationKeyTypeDescriptor(
                    first.Type,
                    iterations.Count,
                    CountIterationsByKind(group, query.Statuses),
                    iterations);
            })
            .Where(keyType => keyType.Iterations.Count > 0)
            .OrderByDescending(keyType => keyType.IterationCount)
            .ThenBy(keyType => keyType.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return Task.FromResult(new WorkIterationKeyTypeQueryResult(page, matches.Count, normalizedSkip, normalizedTake));
    }

    public Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (criteria is null || IsWholeSystemStatusSummary(criteria))
        {
            return Task.FromResult(CreateStatusSummary(this.index.CountByState()));
        }

        var workers = this.GetCandidateWorkers(criteria)
            .Select(worker => new
            {
                Record = worker,
                Overview = worker.ToOverviewItem(),
            })
            .Where(worker => Matches(worker.Overview, criteria) && Matches(worker.Record, criteria.Configuration))
            .Select(worker => worker.Overview)
            .ToList();
        var counts = workers
            .GroupBy(worker => worker.State)
            .ToDictionary(group => group.Key, group => group.Count());
        var active = workers.Count(worker => IsActiveForSummary(worker.State));
        var final = workers.Count(worker => WorkerStateMachine.IsFinal(worker.State));
        return Task.FromResult(new WorkerStatusSummary(
            workers.Count,
            active,
            final,
            counts));
    }

    public Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateSystemQueryContext(criteria).CreateDetails());
    }

    public Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateSystemQueryContext(criteria).CreateThroughput(throughput));
    }

    public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateSystemQueryContext(criteria).CreateThroughputSummary(throughput));
    }

    public Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateSystemQueryContext(criteria).WorkerCounts);
    }

    public Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateSystemQueryContext(criteria).IterationCounts);
    }

    public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkIterationKeyTypeFacetQueryResult(
            this.CreateSystemQueryContext(criteria).CommonKeyTypes));
    }

    public Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.CreateSystemQueryContext(criteria).CreateFailedWorkers());
    }

    public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkerIterationOverviewQueryResult(
            this.CreateSystemQueryContext(criteria).FailedIterations));
    }

    public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkerIterationOverviewQueryResult(
            this.CreateSystemQueryContext(criteria).CompletedIterations));
    }

    private static WorkerStatusSummary CreateStatusSummary(IReadOnlyDictionary<WorkerState, int> counts)
    {
        var total = counts.Values.Sum();
        var final = counts
            .Where(count => WorkerStateMachine.IsFinal(count.Key))
            .Sum(count => count.Value);
        var active = counts
            .Where(count => IsActiveForSummary(count.Key))
            .Sum(count => count.Value);
        return new WorkerStatusSummary(
            total,
            active,
            final,
            counts);
    }

    private static bool IsActiveForSummary(WorkerState state)
        => !WorkerStateMachine.IsFinal(state) && state != WorkerState.Failed;

    private static bool IsWholeSystemStatusSummary(WorkerCriteria query)
        => query.DefinitionId is null &&
            string.IsNullOrWhiteSpace(query.DefinitionName) &&
            string.IsNullOrWhiteSpace(query.Category) &&
            query.SubjectId is null &&
            query.ConcurrencyKey is null &&
            query.Identifier is null &&
            query.States is null &&
            query.Configuration is null &&
            query.CreatedFrom is null &&
            query.CreatedTo is null &&
            query.UpdatedFrom is null &&
            query.UpdatedTo is null;

    private WorkSystemQueryContext CreateSystemQueryContext(WorkSystemCriteria? criteria)
        => new(this, criteria);

    private WorkSystemWorkerCounts CreateSystemWorkerCounts(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var counts = this.index.CountByState(definitionIds);
        var final = counts
            .Where(count => WorkerStateMachine.IsFinal(count.Key))
            .Sum(count => count.Value);
        var active = counts
            .Where(count => IsActiveForSummary(count.Key))
            .Sum(count => count.Value);
        return new WorkSystemWorkerCounts(
            this.index.ActiveOrQueuedDefinitionCount(definitionIds),
            active,
            final,
            counts.GetValueOrDefault(WorkerState.Failed),
            counts,
            this.index.OldestQueuedAt(definitionIds));
    }

    private WorkSystemIterationCounts CreateSystemIterationCounts(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var counts = this.iterationIndex.CountByStatus(definitionIds);
        return new WorkSystemIterationCounts(
            counts.GetValueOrDefault(WorkCompletionStatus.Executing),
            counts.GetValueOrDefault(WorkCompletionStatus.Completed),
            counts.GetValueOrDefault(WorkCompletionStatus.Failed),
            counts.GetValueOrDefault(WorkCompletionStatus.Canceled),
            counts);
    }

    private IReadOnlyList<WorkIterationKeyTypeFacet> CreateSystemCommonKeyTypes(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => [.. this.iterationIndex.CommonKeyTypes(SystemCommonKeyTypeCount, definitionIds)
            .Select(keyType => new WorkIterationKeyTypeFacet(
                keyType.Type,
                keyType.IterationCount,
                keyType.IterationCountByKind))];

    private WorkSystemThroughput CreateSystemThroughput(
        IReadOnlySet<WorkDefinitionId>? definitionIds = null,
        WorkThroughputCriteria? throughputQuery = null)
        => this.metrics.GetThroughput(throughputQuery, definitionIds);

    private WorkSystemThroughputSummary CreateSystemThroughputSummary(
        IReadOnlySet<WorkDefinitionId>? definitionIds = null,
        WorkThroughputCriteria? throughputQuery = null)
        => this.metrics.GetThroughputSummary(throughputQuery, definitionIds);

    private IReadOnlyList<WorkerOverviewItem> CreateSystemFailedWorkers(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => [.. this.GetOverviewItems(this.index.ByState(WorkerState.Failed, definitionIds))
            .Take(SystemWorkerListSize)];

    private IReadOnlyList<WorkerIterationOverviewItem> CreateSystemFailedIterations(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => this.iterationIndex.RecentByStatus(WorkCompletionStatus.Failed, SystemIterationListSize, definitionIds);

    private IReadOnlyList<WorkerIterationOverviewItem> CreateSystemCompletedIterations(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
        => this.iterationIndex.RecentByStatus(WorkCompletionStatus.Completed, SystemIterationListSize, definitionIds);

    private IReadOnlyList<WorkerOverviewItem> GetOverviewItems(
        IEnumerable<WorkerId> workerIds,
        IReadOnlySet<WorkerState>? states = null)
        => [.. workerIds
            .Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker : null)
            .OfType<WorkerRecord>()
            .Select(worker => worker.ToOverviewItem())
            .Where(worker => states is null || states.Contains(worker.State))
            .OrderByDescending(worker => worker.UpdatedAt)];

    private HashSet<WorkDefinitionId>? ResolveDefinitionScope(WorkSystemCriteria? query)
    {
        if (query is null ||
            (query.DefinitionId is null &&
            string.IsNullOrWhiteSpace(query.DefinitionName) &&
            string.IsNullOrWhiteSpace(query.Category)))
        {
            return null;
        }

        return this.GetDefinitionScopeCandidates(query)
            .Where(definition => Matches(definition, query))
            .Select(definition => definition.Id)
            .ToHashSet();
    }

    private IEnumerable<WorkDefinition> GetDefinitionScopeCandidates(WorkSystemCriteria query)
    {
        if (query.DefinitionId is { } definitionId)
        {
            return this.catalog.TryGet(definitionId, out var definition) ? [definition] : [];
        }

        if (!string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            return this.catalog.TryGet(query.DefinitionName, out var definition) ? [definition] : [];
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            return this.catalog.ListByCategory(query.Category, query.IncludeSubcategories);
        }

        return this.catalog.Definitions;
    }

    private IEnumerable<WorkerRecord> GetCandidateWorkers(WorkerCriteria query)
    {
        if (!string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            if (!this.catalog.TryGet(query.DefinitionName, out var definition))
            {
                return [];
            }

            query = query with
            {
                DefinitionId = definition.Id,
            };
        }

        IReadOnlySet<WorkDefinitionId>? definitionIds = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            definitionIds = this.catalog
                .ListByCategory(query.Category, query.IncludeSubcategories)
                .Select(definition => definition.Id)
                .ToHashSet();
        }

        var candidateIds = this.index.FindBestCandidates(query, definitionIds);
        return candidateIds is null
            ? this.workers.Values
            : candidateIds.Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker : null)
                .OfType<WorkerRecord>();
    }

    private WorkInfo CreateWorkInfo(WorkDefinition definition)
    {
        var summaries = this.index.ByDefinition(definition.Id)
            .Select(workerId => this.workers.TryGetValue(workerId, out var worker) ? worker.ToSummary() : null)
            .OfType<WorkerSummary>()
            .ToList();
        var rollup = CreateRollup(summaries);
        return new WorkInfo(definition, StatusFor(rollup), rollup);
    }

    private static WorkerRollup CreateRollup(List<WorkerSummary> summaries)
    {
        var completed = summaries.Count(worker => worker.State == WorkerState.Completed);
        var canceled = summaries.Count(worker => worker.State == WorkerState.Canceled);
        return new WorkerRollup(
            summaries.Count,
            summaries.Count(worker => IsActiveForSummary(worker.State)),
            summaries.Count(worker => worker.State == WorkerState.Queued),
            summaries.Count(worker => worker.State is WorkerState.Running or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling),
            summaries.Count(worker => worker.State == WorkerState.Waiting),
            summaries.Count(worker => worker.State == WorkerState.Paused),
            summaries.Count(worker => worker.State == WorkerState.Failed),
            canceled,
            completed,
            summaries.Count == 0 ? null : summaries.Max(worker => worker.UpdatedAt));
    }

    private static WorkDefinitionStatus StatusFor(WorkerRollup rollup)
    {
        if (rollup.Total == 0 || rollup.Total == rollup.Completed + rollup.Canceled)
        {
            return WorkDefinitionStatus.Inactive;
        }

        if (rollup.Failed > 0 && rollup.Active == rollup.Failed)
        {
            return WorkDefinitionStatus.Critical;
        }

        if (rollup.Failed > 0 || rollup.Paused > 0)
        {
            return WorkDefinitionStatus.NeedsAttention;
        }

        return rollup.Active > 0 ? WorkDefinitionStatus.Healthy : WorkDefinitionStatus.Unknown;
    }

    private static bool Matches(WorkerOverviewItem worker, WorkerCriteria query)
        => (query.DefinitionId is null || worker.DefinitionId == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(worker.DefinitionName, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(worker.Category, query.Category, query.IncludeSubcategories)) &&
            (query.SubjectId is null || worker.SubjectId == query.SubjectId) &&
            (query.ConcurrencyKey is null || worker.ConcurrencyKey == query.ConcurrencyKey) &&
            (query.Identifier is null || worker.Identifiers.Contains(query.Identifier.Value)) &&
            (query.States is null || query.States.Contains(worker.State)) &&
            (query.CreatedFrom is null || worker.CreatedAt >= query.CreatedFrom) &&
            (query.CreatedTo is null || worker.CreatedAt <= query.CreatedTo) &&
            (query.UpdatedFrom is null || worker.UpdatedAt >= query.UpdatedFrom) &&
            (query.UpdatedTo is null || worker.UpdatedAt <= query.UpdatedTo);

    private static bool Matches(WorkerRecord worker, WorkerConfigurationCriteria? query)
        => query is null ||
            (query.RecurrenceEnabled is null || worker.Configuration.Recurrence.IsEnabled == query.RecurrenceEnabled) &&
            (query.ConcurrencyEnabled is null || worker.Configuration.Concurrency.IsEnabled == query.ConcurrencyEnabled) &&
            (query.ProfilingEnabled is null || worker.Options.ProfilingEnabled == query.ProfilingEnabled);

    private static bool Matches(WorkerIterationIndex.IndexedWorkerIteration iteration, WorkerIterationCriteria query)
        => (query.WorkerId is null || iteration.WorkerId == query.WorkerId) &&
            (query.DefinitionId is null || iteration.DefinitionId == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(iteration.DefinitionName, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(iteration.Category, query.Category, includeSubcategories: true)) &&
            (query.SubjectId is null || iteration.SubjectId == query.SubjectId) &&
            (query.ConcurrencyKey is null || iteration.ConcurrencyKey == query.ConcurrencyKey) &&
            (query.Identifier is null || iteration.Identifiers.Contains(query.Identifier.Value)) &&
            (query.Statuses is null || query.Statuses.Contains(iteration.Status)) &&
            (query.StartedFrom is null || iteration.StartedAt >= query.StartedFrom) &&
            (query.StartedTo is null || iteration.StartedAt <= query.StartedTo) &&
            (query.CompletedFrom is null || iteration.CompletedAt >= query.CompletedFrom) &&
            (query.CompletedTo is null || iteration.CompletedAt <= query.CompletedTo);

    private static bool Matches(WorkDefinition definition, WorkDefinitionCriteria query)
        => (query.Id is null || definition.Id == query.Id) &&
            (string.IsNullOrWhiteSpace(query.Name) || string.Equals(definition.Name, query.Name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(definition.Category, query.Category, query.IncludeSubcategories)) &&
            (string.IsNullOrWhiteSpace(query.Search) ||
                definition.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                (definition.Description?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool Matches(WorkDefinition definition, WorkSystemCriteria query)
        => (query.DefinitionId is null || definition.Id == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(definition.Name, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(definition.Category, query.Category, query.IncludeSubcategories));

    private static bool Matches(WorkerIndex.IndexedWorkKey key, WorkerKeyCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Value) || string.Equals(key.Value, query.Value, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: true);

    private static bool Matches(WorkerIndex.IndexedWorkKey key, WorkerKeyTypeCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: false);

    private static bool Matches(WorkerIterationIndex.IndexedWorkIterationKey key, WorkIterationKeyCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Value) || string.Equals(key.Value, query.Value, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: true);

    private static bool Matches(WorkerIterationIndex.IndexedWorkIterationKey key, WorkIterationKeyTypeCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: false);

    private Dictionary<WorkKeyKind, int> CountWorkersByKind(
        IEnumerable<WorkerIndex.IndexedWorkKey> keys,
        IReadOnlySet<WorkerState>? states)
        => keys
            .GroupBy(key => key.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Count = this.GetOverviewItems(group.SelectMany(key => key.WorkerIds).Distinct(), states).Count,
            })
            .Where(count => count.Count > 0)
            .ToDictionary(count => count.Kind, count => count.Count);

    private Dictionary<WorkKeyKind, int> CountIterationsByKind(
        IEnumerable<WorkerIterationIndex.IndexedWorkIterationKey> keys,
        IReadOnlySet<WorkCompletionStatus>? statuses)
        => keys
            .GroupBy(key => key.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                this.iterationIndex.GetOverviewItems(
                    group.SelectMany(key => key.IterationReferences).Distinct(),
                    statuses).Count,
            })
            .Where(count => count.Count > 0)
            .ToDictionary(count => count.Kind, count => count.Count);

    private static bool MatchesWorkKeySearch(
        string type,
        string value,
        string? search,
        bool includeValue)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var terms = SearchTerms(search);
        if (terms.Count == 0)
        {
            return true;
        }

        return terms.All(term =>
            type.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (includeValue && value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> SearchTerms(string search)
    {
        var terms = new List<string>();
        foreach (var term in search.Split(
            [' ', '\t', '\r', '\n', '.', ',', ':', ';', '-', '_', '/', '\\', '#', '=', '&', '?'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsIgnoredWorkKeySearchTerm(term))
            {
                continue;
            }

            terms.Add(term);
        }

        return terms;
    }

    private static bool IsIgnoredWorkKeySearchTerm(string term)
        => term.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("for", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("keys", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("the", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("work", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("worker", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("workers", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeWorkKeyTake(int take)
        => take <= 0 ? WorkerKeyCriteria.DefaultTake : Math.Min(take, WorkerKeyCriteria.MaximumTake);

    private static int NormalizeWorkIterationKeyTake(int take)
        => take <= 0 ? WorkIterationKeyCriteria.DefaultTake : Math.Min(take, WorkIterationKeyCriteria.MaximumTake);

    private static bool CategoryMatches(string actual, string expected, bool includeSubcategories)
        => includeSubcategories
            ? actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                actual.StartsWith($"{expected}:", StringComparison.OrdinalIgnoreCase)
            : actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<WorkerOverviewItem> Sort(
        IEnumerable<WorkerOverviewItem> workers,
        WorkerCriteriaSort sort,
        WorkCriteriaSortDirection direction)
    {
        var ascending = direction == WorkCriteriaSortDirection.Ascending;
        return sort switch
        {
            WorkerCriteriaSort.UpdatedAt => ascending ? workers.OrderBy(worker => worker.UpdatedAt) : workers.OrderByDescending(worker => worker.UpdatedAt),
            WorkerCriteriaSort.DefinitionName => ascending ? workers.OrderBy(worker => worker.DefinitionName) : workers.OrderByDescending(worker => worker.DefinitionName),
            WorkerCriteriaSort.State => ascending ? workers.OrderBy(worker => worker.State) : workers.OrderByDescending(worker => worker.State),
            _ => ascending ? workers.OrderBy(worker => worker.CreatedAt) : workers.OrderByDescending(worker => worker.CreatedAt),
        };
    }

    private static IEnumerable<WorkerIterationOverviewItem> Sort(
        IEnumerable<WorkerIterationOverviewItem> iterations,
        WorkerIterationCriteriaSort sort,
        WorkCriteriaSortDirection direction)
    {
        var ascending = direction == WorkCriteriaSortDirection.Ascending;
        return sort switch
        {
            WorkerIterationCriteriaSort.StartedAt => ascending ? iterations.OrderBy(iteration => iteration.StartedAt) : iterations.OrderByDescending(iteration => iteration.StartedAt),
            WorkerIterationCriteriaSort.ExecutionDuration => ascending ? iterations.OrderBy(iteration => iteration.ExecutionDuration) : iterations.OrderByDescending(iteration => iteration.ExecutionDuration),
            WorkerIterationCriteriaSort.DefinitionName => ascending ? iterations.OrderBy(iteration => iteration.DefinitionName) : iterations.OrderByDescending(iteration => iteration.DefinitionName),
            WorkerIterationCriteriaSort.Status => ascending ? iterations.OrderBy(iteration => iteration.Status) : iterations.OrderByDescending(iteration => iteration.Status),
            _ => ascending ? iterations.OrderBy(iteration => iteration.CompletedAt) : iterations.OrderByDescending(iteration => iteration.CompletedAt),
        };
    }

}

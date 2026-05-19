namespace Workable;

internal sealed class WorkSystemReadModelQueryService(
    WorkSystemCatalog catalog,
    Func<WorkSystemState> getSystemState,
    string? workSystemName,
    WorkSystemReadModel readModel,
    InMemoryWorkMetricsSink metrics,
    Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getWorkerDetail = null,
    Func<WorkerIterationReference, CancellationToken, Task<WorkerIterationSnapshot?>>? getIterationDetail = null,
    WorkSystemReadModelSnapshot? snapshot = null) : IWorkSnapshotQueryService
{
    private const int SystemWorkerListSize = 5;
    private const int SystemIterationListSize = 5;
    private const int SystemCommonKeyTypeCount = 10;

    private static readonly WorkerState[] ActiveOrQueuedDefinitionStates =
    [
        WorkerState.Queued,
        WorkerState.Running,
        WorkerState.Waiting,
        WorkerState.Retrying,
        WorkerState.Pausing,
        WorkerState.Canceling,
        WorkerState.Paused,
    ];

    private readonly WorkSystemCatalog catalog = catalog;
    private readonly Func<WorkSystemState> getSystemState = getSystemState;
    private readonly string? workSystemName = workSystemName;
    private readonly WorkSystemReadModel readModel = readModel;
    private readonly InMemoryWorkMetricsSink metrics = metrics;
    private readonly WorkSystemReadModelSnapshot? capturedSnapshot = snapshot;
    private Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>> getWorkerDetail = getWorkerDetail ?? MissingWorkerDetail;
    private Func<WorkerIterationReference, CancellationToken, Task<WorkerIterationSnapshot?>> getIterationDetail = getIterationDetail ?? MissingIterationDetail;

    internal void UseDetailReaders(
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>> getWorker,
        Func<WorkerIterationReference, CancellationToken, Task<WorkerIterationSnapshot?>> getIteration)
    {
        ArgumentNullException.ThrowIfNull(getWorker);
        ArgumentNullException.ThrowIfNull(getIteration);

        this.getWorkerDetail = getWorker;
        this.getIterationDetail = getIteration;
    }

    public IWorkQueryService BeginRead()
    {
        return new WorkSystemReadModelQueryService(
            this.catalog,
            this.getSystemState,
            this.workSystemName,
            this.readModel,
            this.metrics,
            this.getWorkerDetail,
            this.getIterationDetail,
            this.readModel.Current);
    }

    public async Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.capturedSnapshot is { } snapshot && !snapshot.WorkersById.ContainsKey(workerId))
        {
            return null;
        }

        return await this.getWorkerDetail(workerId, cancellationToken);
    }

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.capturedSnapshot is { } snapshot && !snapshot.IterationsByReference.ContainsKey(iteration))
        {
            return null;
        }

        return await this.getIterationDetail(iteration, cancellationToken);
    }

    public async Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var query = criteria ?? new WorkerCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkerTake(query.Take);
        if (!this.TryNormalizeWorkerDefinitionName(query, out query))
        {
            return new WorkerQueryResult([], 0, normalizedSkip, normalizedTake);
        }

        var filtered = this.GetCandidateWorkers(snapshot, query)
            .Where(worker => Matches(worker, query));

        var sorted = Sort(filtered.Select(worker => worker.Overview), query.Sort, query.Direction);
        var materialized = sorted.ToArray();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkerQueryResult(page, materialized.Length, normalizedSkip, normalizedTake);
    }

    public async Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var query = criteria ?? new WorkerIterationCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkerIterationTake(query.Take);
        if (!this.TryNormalizeIterationDefinitionName(query, out query))
        {
            return new WorkerIterationQueryResult([], 0, normalizedSkip, normalizedTake);
        }

        var filtered = this.GetCandidateIterations(snapshot, query)
            .Where(iteration => Matches(iteration, query));

        var sorted = Sort(filtered.Select(iteration => iteration.Overview), query.Sort, query.Direction);
        var materialized = sorted.ToArray();
        var page = materialized
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkerIterationQueryResult(page, materialized.Length, normalizedSkip, normalizedTake);
    }

    public async Task<WorkInfo?> WorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        if (!this.catalog.TryGet(definitionId, out var definition))
        {
            return null;
        }

        return this.CreateWorkInfo(snapshot, definition);
    }

    public async Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var snapshot = await this.GetSnapshot(cancellationToken);
        if (!this.catalog.TryGet(name, out var definition))
        {
            return null;
        }

        return this.CreateWorkInfo(snapshot, definition);
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
            .ThenBy(definition => definition.Name)
            .ToArray();
        return Task.FromResult(new WorkDefinitionQueryResult(definitions));
    }

    public async Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var query = criteria ?? new WorkerKeyCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkKeyTake(query.Take);
        var matches = snapshot.WorkerKeys
            .Where(key => Matches(key, query))
            .GroupBy(key => new WorkKeyGroupKey(key.Kind, NormalizeType(key.Type), NormalizeValue(key.Value)))
            .Select(group =>
            {
                var first = group.First();
                return new WorkerKeyDescriptor(
                    first.Kind,
                    first.Type,
                    first.Value,
                    CreateWorkerOverviewList(group.Select(key => key.Worker), query.States));
            })
            .Where(key => key.Workers.Count > 0)
            .OrderBy(key => key.Kind)
            .ThenBy(key => key.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkerKeyQueryResult(page, matches.Length, normalizedSkip, normalizedTake);
    }

    public async Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var query = criteria ?? new WorkerKeyTypeCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkKeyTake(query.Take);
        var matches = snapshot.WorkerKeys
            .Where(key => Matches(key, query))
            .GroupBy(key => NormalizeType(key.Type))
            .Select(group =>
            {
                var workers = CreateWorkerOverviewList(group.Select(key => key.Worker), query.States);
                return new WorkerKeyTypeDescriptor(
                    group.First().Type,
                    workers.Count,
                    CountWorkersByKind(group, query.States),
                    workers);
            })
            .Where(type => type.WorkerCount > 0)
            .OrderByDescending(type => type.WorkerCount)
            .ThenBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkerKeyTypeQueryResult(page, matches.Length, normalizedSkip, normalizedTake);
    }

    public async Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var query = criteria ?? new WorkIterationKeyCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkIterationKeyTake(query.Take);
        var matches = snapshot.IterationKeys
            .Where(key => Matches(key, query))
            .GroupBy(key => new WorkKeyGroupKey(key.Kind, NormalizeType(key.Type), NormalizeValue(key.Value)))
            .Select(group =>
            {
                var first = group.First();
                return new WorkIterationKeyDescriptor(
                    first.Kind,
                    first.Type,
                    first.Value,
                    CreateIterationOverviewList(group.Select(key => key.Iteration), query.Statuses));
            })
            .Where(key => key.Iterations.Count > 0)
            .OrderBy(key => key.Kind)
            .ThenBy(key => key.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkIterationKeyQueryResult(page, matches.Length, normalizedSkip, normalizedTake);
    }

    public async Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var query = criteria ?? new WorkIterationKeyTypeCriteria();
        var normalizedSkip = Math.Max(0, query.Skip);
        var normalizedTake = NormalizeWorkIterationKeyTake(query.Take);
        var matches = snapshot.IterationKeys
            .Where(key => Matches(key, query))
            .GroupBy(key => NormalizeType(key.Type))
            .Select(group =>
            {
                var iterations = CreateIterationOverviewList(group.Select(key => key.Iteration), query.Statuses);
                return new WorkIterationKeyTypeDescriptor(
                    group.First().Type,
                    iterations.Count,
                    CountIterationsByKind(group, query.Statuses),
                    iterations);
            })
            .Where(type => type.IterationCount > 0)
            .OrderByDescending(type => type.IterationCount)
            .ThenBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = matches
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();

        return new WorkIterationKeyTypeQueryResult(page, matches.Length, normalizedSkip, normalizedTake);
    }

    public async Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        if (criteria is null || IsWholeSystemStatusSummary(criteria))
        {
            return CreateStatusSummary(CountWorkersByState(snapshot.Workers, definitionIds: null));
        }

        if (!this.TryNormalizeWorkerDefinitionName(criteria, out var query))
        {
            return new WorkerStatusSummary(0, 0, 0, new Dictionary<WorkerState, int>());
        }

        var workers = this.GetCandidateWorkers(snapshot, query)
            .Where(worker => Matches(worker, query))
            .ToArray();
        return CreateStatusSummary(CountWorkersByState(workers, definitionIds: null));
    }

    public async Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var definitionIds = this.ResolveDefinitionScope(criteria);
        var workerCounts = CreateSystemWorkerCounts(snapshot, definitionIds);
        var iterationCounts = CreateSystemIterationCounts(snapshot, definitionIds);
        return new WorkSystemDetails(
            this.workSystemName,
            this.getSystemState(),
            workerCounts.DefinitionCount,
            workerCounts.ActiveWorkerCount,
            workerCounts.FinalWorkerCount,
            workerCounts.FailedWorkerCount,
            workerCounts.WorkerCountByState,
            workerCounts.OldestQueuedAt,
            iterationCounts.CurrentIterationCount,
            iterationCounts.CompletedIterationCount,
            iterationCounts.FailedIterationCount,
            iterationCounts.CanceledIterationCount,
            iterationCounts.IterationCountByStatus,
            CreateSystemCommonKeyTypes(snapshot, definitionIds),
            criteria?.IncludeThroughput == true ? this.CreateSystemThroughput(definitionIds, throughputQuery: null) : null,
            CreateSystemFailedWorkers(snapshot, definitionIds),
            CreateSystemRecentIterations(snapshot, WorkCompletionStatus.Failed, SystemIterationListSize, definitionIds),
            CreateSystemRecentIterations(snapshot, WorkCompletionStatus.Completed, SystemIterationListSize, definitionIds));
    }

    public async Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
    {
        await this.FlushIfLive(cancellationToken);
        return this.CreateSystemThroughput(this.ResolveDefinitionScope(criteria), throughput);
    }

    public async Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
    {
        await this.FlushIfLive(cancellationToken);
        return this.metrics.GetThroughputSummary(throughput, this.ResolveDefinitionScope(criteria));
    }

    public async Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        return CreateSystemWorkerCounts(snapshot, this.ResolveDefinitionScope(criteria));
    }

    public async Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        return CreateSystemIterationCounts(snapshot, this.ResolveDefinitionScope(criteria));
    }

    public async Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        return new WorkIterationKeyTypeFacetQueryResult(
            CreateSystemCommonKeyTypes(snapshot, this.ResolveDefinitionScope(criteria)));
    }

    public async Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        var definitionIds = this.ResolveDefinitionScope(criteria);
        var counts = CreateSystemWorkerCounts(snapshot, definitionIds);
        return new WorkSystemFailedWorkers(
            counts.ActiveWorkerCount,
            counts.FinalWorkerCount,
            counts.FailedWorkerCount,
            counts.WorkerCountByState,
            CreateSystemFailedWorkers(snapshot, definitionIds));
    }

    public async Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        return new WorkerIterationOverviewQueryResult(
            CreateSystemRecentIterations(
                snapshot,
                WorkCompletionStatus.Failed,
                SystemIterationListSize,
                this.ResolveDefinitionScope(criteria)));
    }

    public async Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetSnapshot(cancellationToken);
        return new WorkerIterationOverviewQueryResult(
            CreateSystemRecentIterations(
                snapshot,
                WorkCompletionStatus.Completed,
                SystemIterationListSize,
                this.ResolveDefinitionScope(criteria)));
    }

    private async ValueTask<WorkSystemReadModelSnapshot> GetSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.capturedSnapshot is { } snapshot)
        {
            return snapshot;
        }

        await this.readModel.Flush(cancellationToken);
        return this.readModel.Current;
    }

    private async ValueTask FlushIfLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (this.capturedSnapshot is not null)
        {
            return;
        }

        await this.readModel.Flush(cancellationToken);
    }

    private static Task<WorkerSnapshot?> MissingWorkerDetail(
        WorkerId _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<WorkerSnapshot?>(null);
    }

    private static Task<WorkerIterationSnapshot?> MissingIterationDetail(
        WorkerIterationReference _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<WorkerIterationSnapshot?>(null);
    }

    private bool TryNormalizeWorkerDefinitionName(
        WorkerCriteria query,
        out WorkerCriteria normalized)
    {
        normalized = query;
        if (string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            return true;
        }

        if (!this.catalog.TryGet(query.DefinitionName, out var definition))
        {
            return false;
        }

        normalized = query with
        {
            DefinitionId = definition.Id,
        };
        return true;
    }

    private bool TryNormalizeIterationDefinitionName(
        WorkerIterationCriteria query,
        out WorkerIterationCriteria normalized)
    {
        normalized = query;
        if (string.IsNullOrWhiteSpace(query.DefinitionName))
        {
            return true;
        }

        if (!this.catalog.TryGet(query.DefinitionName, out var definition))
        {
            return false;
        }

        normalized = query with
        {
            DefinitionId = definition.Id,
        };
        return true;
    }

    private IEnumerable<WorkerReadModelWorker> GetCandidateWorkers(
        WorkSystemReadModelSnapshot snapshot,
        WorkerCriteria query)
    {
        var candidates = new List<IReadOnlyList<WorkerReadModelWorker>>();

        if (!this.TryAddDefinitionCandidates(snapshot, query, candidates) ||
            !TryAddCandidate(candidates, snapshot.WorkersBySubject, query.SubjectId) ||
            !TryAddCandidate(candidates, snapshot.WorkersByConcurrencyKey, query.ConcurrencyKey) ||
            !TryAddCandidate(candidates, snapshot.WorkersByIdentifier, query.Identifier) ||
            !TryAddCandidate(candidates, snapshot.WorkersByDefinition, query.DefinitionId) ||
            !TryAddConfigurationCandidates(snapshot, query.Configuration, candidates))
        {
            return [];
        }

        if (query.States is { Count: > 0 } states)
        {
            var workers = Combine(snapshot.WorkersByState, states, worker => worker.Id);
            if (workers.Count == 0)
            {
                return [];
            }

            candidates.Add(workers);
        }

        return candidates.Count == 0
            ? snapshot.Workers
            : candidates.MinBy(candidate => candidate.Count) ?? [];
    }

    private static bool TryAddConfigurationCandidates(
        WorkSystemReadModelSnapshot snapshot,
        WorkerConfigurationCriteria? query,
        List<IReadOnlyList<WorkerReadModelWorker>> candidates)
        => query is null ||
            (TryAddCandidate(candidates, snapshot.WorkersByRecurrenceEnabled, query.RecurrenceEnabled) &&
            TryAddCandidate(candidates, snapshot.WorkersByConcurrencyEnabled, query.ConcurrencyEnabled) &&
            TryAddCandidate(candidates, snapshot.WorkersByProfilingEnabled, query.ProfilingEnabled));

    private bool TryAddDefinitionCandidates(
        WorkSystemReadModelSnapshot snapshot,
        WorkerCriteria query,
        List<IReadOnlyList<WorkerReadModelWorker>> candidates)
    {
        var definitionIds = this.ResolveWorkerDefinitionScope(query);
        if (definitionIds is null)
        {
            return true;
        }

        if (definitionIds.Count == 0)
        {
            return false;
        }

        var workers = Combine(snapshot.WorkersByDefinition, definitionIds, worker => worker.Id);
        if (workers.Count == 0)
        {
            return false;
        }

        candidates.Add(workers);
        return true;
    }

    private IEnumerable<WorkerReadModelIteration> GetCandidateIterations(
        WorkSystemReadModelSnapshot snapshot,
        WorkerIterationCriteria query)
    {
        var candidates = new List<IReadOnlyList<WorkerReadModelIteration>>();

        if (!this.TryAddIterationDefinitionCandidates(snapshot, query, candidates) ||
            !TryAddCandidate(candidates, snapshot.IterationsByWorker, query.WorkerId) ||
            !TryAddCandidate(candidates, snapshot.IterationsBySubject, query.SubjectId) ||
            !TryAddCandidate(candidates, snapshot.IterationsByConcurrencyKey, query.ConcurrencyKey) ||
            !TryAddCandidate(candidates, snapshot.IterationsByIdentifier, query.Identifier) ||
            !TryAddCandidate(candidates, snapshot.IterationsByDefinition, query.DefinitionId))
        {
            return [];
        }

        if (query.Statuses is { Count: > 0 } statuses)
        {
            var iterations = Combine(snapshot.IterationsByStatus, statuses, iteration => iteration.Reference);
            if (iterations.Count == 0)
            {
                return [];
            }

            candidates.Add(iterations);
        }

        return candidates.Count == 0
            ? snapshot.Iterations
            : candidates.MinBy(candidate => candidate.Count) ?? [];
    }

    private bool TryAddIterationDefinitionCandidates(
        WorkSystemReadModelSnapshot snapshot,
        WorkerIterationCriteria query,
        List<IReadOnlyList<WorkerReadModelIteration>> candidates)
    {
        var definitionIds = this.ResolveIterationDefinitionScope(query);
        if (definitionIds is null)
        {
            return true;
        }

        if (definitionIds.Count == 0)
        {
            return false;
        }

        var iterations = Combine(snapshot.IterationsByDefinition, definitionIds, iteration => iteration.Reference);
        if (iterations.Count == 0)
        {
            return false;
        }

        candidates.Add(iterations);
        return true;
    }

    private HashSet<WorkDefinitionId>? ResolveWorkerDefinitionScope(WorkerCriteria query)
    {
        if (query.DefinitionId is { } definitionId)
        {
            return [definitionId];
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            return this.catalog
                .ListByCategory(query.Category, query.IncludeSubcategories)
                .Select(definition => definition.Id)
                .ToHashSet();
        }

        return null;
    }

    private HashSet<WorkDefinitionId>? ResolveIterationDefinitionScope(WorkerIterationCriteria query)
    {
        if (query.DefinitionId is { } definitionId)
        {
            return [definitionId];
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            return this.catalog
                .ListByCategory(query.Category, includeSubcategories: true)
                .Select(definition => definition.Id)
                .ToHashSet();
        }

        return null;
    }

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

    private WorkInfo CreateWorkInfo(
        WorkSystemReadModelSnapshot snapshot,
        WorkDefinition definition)
    {
        var workers = snapshot.WorkersByDefinition.TryGetValue(definition.Id, out var definitionWorkers)
            ? definitionWorkers
            : [];
        var rollup = CreateRollup(workers);
        return new WorkInfo(definition, StatusFor(rollup), rollup);
    }

    private WorkSystemThroughput CreateSystemThroughput(
        IReadOnlySet<WorkDefinitionId>? definitionIds,
        WorkThroughputCriteria? throughputQuery)
        => this.metrics.GetThroughput(throughputQuery, definitionIds);

    private static WorkSystemWorkerCounts CreateSystemWorkerCounts(
        WorkSystemReadModelSnapshot snapshot,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var workers = FilterByDefinition(snapshot.Workers, definitionIds).ToArray();
        var counts = CountWorkersByState(workers, definitionIds: null);
        return new WorkSystemWorkerCounts(
            workers
                .Where(worker => ActiveOrQueuedDefinitionStates.Contains(worker.State))
                .Select(worker => worker.DefinitionId)
                .Distinct()
                .Count(),
            counts
                .Where(count => IsActiveForSummary(count.Key))
                .Sum(count => count.Value),
            counts
                .Where(count => WorkerStateMachine.IsFinal(count.Key))
                .Sum(count => count.Value),
            counts.GetValueOrDefault(WorkerState.Failed),
            counts,
            workers
                .Where(worker => worker.State == WorkerState.Queued)
                .Select(worker => (DateTimeOffset?)worker.StateChangedAt)
                .Min());
    }

    private static WorkSystemIterationCounts CreateSystemIterationCounts(
        WorkSystemReadModelSnapshot snapshot,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var counts = FilterByDefinition(snapshot.Iterations, definitionIds)
            .GroupBy(iteration => iteration.Status)
            .ToDictionary(group => group.Key, group => group.Count());
        return new WorkSystemIterationCounts(
            counts.GetValueOrDefault(WorkCompletionStatus.Executing),
            counts.GetValueOrDefault(WorkCompletionStatus.Completed),
            counts.GetValueOrDefault(WorkCompletionStatus.Failed),
            counts.GetValueOrDefault(WorkCompletionStatus.Canceled),
            counts);
    }

    private static IReadOnlyList<WorkIterationKeyTypeFacet> CreateSystemCommonKeyTypes(
        WorkSystemReadModelSnapshot snapshot,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => [.. snapshot.IterationKeys
            .Where(key => definitionIds is null || definitionIds.Contains(key.Iteration.DefinitionId))
            .GroupBy(key => NormalizeType(key.Type))
            .Select(group => new WorkIterationKeyTypeFacet(
                group.First().Type,
                group.Select(key => key.Iteration.Reference).Distinct().Count(),
                CountIterationsByKind(group, statuses: null)))
            .Where(facet => facet.IterationCount > 0)
            .OrderByDescending(facet => facet.IterationCount)
            .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)
            .Take(SystemCommonKeyTypeCount)];

    private static IReadOnlyList<WorkerOverviewItem> CreateSystemFailedWorkers(
        WorkSystemReadModelSnapshot snapshot,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => [.. FilterByDefinition(snapshot.Workers, definitionIds)
            .Where(worker => worker.State == WorkerState.Failed)
            .OrderByDescending(worker => worker.UpdatedAt)
            .Take(SystemWorkerListSize)
            .Select(worker => worker.Overview)];

    private static IReadOnlyList<WorkerIterationOverviewItem> CreateSystemRecentIterations(
        WorkSystemReadModelSnapshot snapshot,
        WorkCompletionStatus status,
        int take,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => [.. FilterByDefinition(snapshot.Iterations, definitionIds)
            .Where(iteration => iteration.Status == status)
            .OrderByDescending(iteration => iteration.CompletedAt)
            .ThenByDescending(iteration => iteration.Sequence)
            .Take(Math.Max(0, take))
            .Select(iteration => iteration.Overview)];

    private static IEnumerable<WorkerReadModelWorker> FilterByDefinition(
        IEnumerable<WorkerReadModelWorker> workers,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? workers
            : definitionIds.Count == 0 ? [] : workers.Where(worker => definitionIds.Contains(worker.DefinitionId));

    private static IEnumerable<WorkerReadModelIteration> FilterByDefinition(
        IEnumerable<WorkerReadModelIteration> iterations,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? iterations
            : definitionIds.Count == 0 ? [] : iterations.Where(iteration => definitionIds.Contains(iteration.DefinitionId));

    private static WorkerRollup CreateRollup(IReadOnlyList<WorkerReadModelWorker> workers)
    {
        var completed = workers.Count(worker => worker.State == WorkerState.Completed);
        var canceled = workers.Count(worker => worker.State == WorkerState.Canceled);
        return new WorkerRollup(
            workers.Count,
            workers.Count(worker => IsActiveForSummary(worker.State)),
            workers.Count(worker => worker.State == WorkerState.Queued),
            workers.Count(worker => worker.State is WorkerState.Running or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling),
            workers.Count(worker => worker.State == WorkerState.Waiting),
            workers.Count(worker => worker.State == WorkerState.Paused),
            workers.Count(worker => worker.State == WorkerState.Failed),
            canceled,
            completed,
            workers.Count == 0 ? null : workers.Max(worker => worker.UpdatedAt));
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

    private static WorkerStatusSummary CreateStatusSummary(IReadOnlyDictionary<WorkerState, int> counts)
    {
        var total = counts.Values.Sum();
        var final = counts
            .Where(count => WorkerStateMachine.IsFinal(count.Key))
            .Sum(count => count.Value);
        var active = counts
            .Where(count => IsActiveForSummary(count.Key))
            .Sum(count => count.Value);
        return new WorkerStatusSummary(total, active, final, counts);
    }

    private static Dictionary<WorkerState, int> CountWorkersByState(
        IEnumerable<WorkerReadModelWorker> workers,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => FilterByDefinition(workers, definitionIds)
            .GroupBy(worker => worker.State)
            .ToDictionary(group => group.Key, group => group.Count());

    private static IReadOnlyList<WorkerOverviewItem> CreateWorkerOverviewList(
        IEnumerable<WorkerReadModelWorker> workers,
        IReadOnlySet<WorkerState>? states)
        => [.. workers
            .Where(worker => states is null || states.Contains(worker.State))
            .DistinctBy(worker => worker.Id)
            .OrderByDescending(worker => worker.UpdatedAt)
            .Select(worker => worker.Overview)];

    private static IReadOnlyList<WorkerIterationOverviewItem> CreateIterationOverviewList(
        IEnumerable<WorkerReadModelIteration> iterations,
        IReadOnlySet<WorkCompletionStatus>? statuses)
        => [.. iterations
            .Where(iteration => statuses is null || statuses.Contains(iteration.Status))
            .DistinctBy(iteration => iteration.Reference)
            .OrderByDescending(iteration => iteration.CompletedAt)
            .Select(iteration => iteration.Overview)];

    private static Dictionary<WorkKeyKind, int> CountWorkersByKind(
        IEnumerable<WorkerReadModelKey> keys,
        IReadOnlySet<WorkerState>? states)
        => keys
            .GroupBy(key => key.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Count = group
                    .Select(key => key.Worker)
                    .Where(worker => states is null || states.Contains(worker.State))
                    .DistinctBy(worker => worker.Id)
                    .Count(),
            })
            .Where(count => count.Count > 0)
            .ToDictionary(count => count.Kind, count => count.Count);

    private static Dictionary<WorkKeyKind, int> CountIterationsByKind(
        IEnumerable<WorkerIterationReadModelKey> keys,
        IReadOnlySet<WorkCompletionStatus>? statuses)
        => keys
            .GroupBy(key => key.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Count = group
                    .Select(key => key.Iteration)
                    .Where(iteration => statuses is null || statuses.Contains(iteration.Status))
                    .DistinctBy(iteration => iteration.Reference)
                    .Count(),
            })
            .Where(count => count.Count > 0)
            .ToDictionary(count => count.Kind, count => count.Count);

    private static bool Matches(WorkerReadModelWorker worker, WorkerCriteria query)
        => (query.DefinitionId is null || worker.DefinitionId == query.DefinitionId) &&
            (string.IsNullOrWhiteSpace(query.DefinitionName) || string.Equals(worker.DefinitionName, query.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) || CategoryMatches(worker.Category, query.Category, query.IncludeSubcategories)) &&
            (query.SubjectId is null || worker.SubjectId == query.SubjectId) &&
            (query.ConcurrencyKey is null || worker.ConcurrencyKey == query.ConcurrencyKey) &&
            (query.Identifier is null || worker.Identifiers.Contains(query.Identifier.Value)) &&
            (query.States is null || query.States.Contains(worker.State)) &&
            (query.Configuration is null || Matches(worker, query.Configuration)) &&
            (query.CreatedFrom is null || worker.CreatedAt >= query.CreatedFrom) &&
            (query.CreatedTo is null || worker.CreatedAt <= query.CreatedTo) &&
            (query.UpdatedFrom is null || worker.UpdatedAt >= query.UpdatedFrom) &&
            (query.UpdatedTo is null || worker.UpdatedAt <= query.UpdatedTo);

    private static bool Matches(WorkerReadModelWorker worker, WorkerConfigurationCriteria query)
        => (query.RecurrenceEnabled is null || worker.RecurrenceEnabled == query.RecurrenceEnabled) &&
            (query.ConcurrencyEnabled is null || worker.ConcurrencyEnabled == query.ConcurrencyEnabled) &&
            (query.ProfilingEnabled is null || worker.ProfilingEnabled == query.ProfilingEnabled);

    private static bool Matches(WorkerReadModelIteration iteration, WorkerIterationCriteria query)
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

    private static bool Matches(WorkerReadModelKey key, WorkerKeyCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Value) || string.Equals(key.Value, query.Value, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: true);

    private static bool Matches(WorkerReadModelKey key, WorkerKeyTypeCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: false);

    private static bool Matches(WorkerIterationReadModelKey key, WorkIterationKeyCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Value) || string.Equals(key.Value, query.Value, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: true);

    private static bool Matches(WorkerIterationReadModelKey key, WorkIterationKeyTypeCriteria query)
        => (query.Kind is null || key.Kind == query.Kind) &&
            (string.IsNullOrWhiteSpace(query.Type) || string.Equals(key.Type, query.Type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesWorkKeySearch(key.Type, key.Value, query.Search, includeValue: false);

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

    private static bool TryAddCandidate<TKey, TValue>(
        List<IReadOnlyList<TValue>> candidates,
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> index,
        TKey? key)
        where TKey : struct
    {
        if (key is null)
        {
            return true;
        }

        if (!index.TryGetValue(key.Value, out var values) || values.Count == 0)
        {
            return false;
        }

        candidates.Add(values);
        return true;
    }

    private static IReadOnlyList<TValue> Combine<TKey, TValue, TIdentity>(
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> index,
        IEnumerable<TKey> keys,
        Func<TValue, TIdentity> identity)
        where TKey : notnull
        where TIdentity : notnull
    {
        var values = new Dictionary<TIdentity, TValue>();
        foreach (var key in keys)
        {
            if (!index.TryGetValue(key, out var indexed))
            {
                continue;
            }

            foreach (var value in indexed)
            {
                values.TryAdd(identity(value), value);
            }
        }

        return [.. values.Values];
    }

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

    private static string NormalizeType(string type)
        => type.ToUpperInvariant();

    private static string NormalizeValue(string value)
        => value.ToUpperInvariant();

    private static int NormalizeWorkerTake(int take)
        => take <= 0 ? WorkerCriteria.DefaultTake : Math.Min(take, WorkerCriteria.MaximumTake);

    private static int NormalizeWorkerIterationTake(int take)
        => take <= 0 ? WorkerIterationCriteria.DefaultTake : Math.Min(take, WorkerIterationCriteria.MaximumTake);

    private static int NormalizeWorkKeyTake(int take)
        => take <= 0 ? WorkerKeyCriteria.DefaultTake : Math.Min(take, WorkerKeyCriteria.MaximumTake);

    private static int NormalizeWorkIterationKeyTake(int take)
        => take <= 0 ? WorkIterationKeyCriteria.DefaultTake : Math.Min(take, WorkIterationKeyCriteria.MaximumTake);

    private readonly record struct WorkKeyGroupKey(
        WorkKeyKind Kind,
        string Type,
        string Value);
}

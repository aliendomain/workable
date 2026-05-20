namespace Workable;

internal sealed class AuthorizedWorkQueryService(
    IWorkCatalog catalog,
    IWorkQueryService inner,
    WorkAuthorizationScope scope) : IWorkQueryService
{
    public async Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        var worker = await inner.Worker(workerId, cancellationToken);
        return worker is not null && scope.CanRead(worker.DefinitionId) ? worker : null;
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
        var workers = await this.ReadAllWorkers(query, cancellationToken);
        var page = workers.Skip(skip).Take(take).ToArray();
        return new WorkerQueryResult(page, workers.Count, skip, take);
    }

    public async Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var query = criteria ?? new WorkerIterationCriteria();
        var skip = Math.Max(0, query.Skip);
        var take = NormalizeTake(query.Take, WorkerIterationCriteria.DefaultTake, WorkerIterationCriteria.MaximumTake);
        var iterations = await this.ReadAllIterations(query, cancellationToken);
        var page = iterations.Skip(skip).Take(take).ToArray();
        return new WorkerIterationQueryResult(page, iterations.Count, skip, take);
    }

    public Task<WorkInfo?> WorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
        => scope.CanRead(definitionId)
            ? inner.WorkInfo(definitionId, cancellationToken)
            : Task.FromResult<WorkInfo?>(null);

    public async Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(name, out var definition) || !scope.CanRead(definition.Id))
        {
            return null;
        }

        return await inner.WorkInfo(name, cancellationToken);
    }

    public async Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.WorkDefinitions(criteria, cancellationToken);
        return new WorkDefinitionQueryResult([.. result.Definitions.Where(definition => scope.CanRead(definition.Id))]);
    }

    public async Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.WorkerKeys(criteria, cancellationToken);
        var keys = result.Keys
            .Select(key => key with
            {
                Workers = [.. key.Workers.Where(worker => scope.CanRead(worker.DefinitionId))],
            })
            .Where(key => key.Workers.Count > 0)
            .ToArray();

        return new WorkerKeyQueryResult(keys, keys.Length, result.Skip, result.Take);
    }

    public async Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.WorkerKeyTypes(criteria, cancellationToken);
        var types = result.Types
            .Select(type =>
            {
                var workers = type.Workers.Where(worker => scope.CanRead(worker.DefinitionId)).ToArray();
                return type with
                {
                    WorkerCount = workers.Length,
                    WorkerCountByKind = new Dictionary<WorkKeyKind, int>(),
                    Workers = workers,
                };
            })
            .Where(type => type.WorkerCount > 0)
            .ToArray();

        return new WorkerKeyTypeQueryResult(types, types.Length, result.Skip, result.Take);
    }

    public async Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.WorkIterationKeys(criteria, cancellationToken);
        var keys = result.Keys
            .Select(key => key with
            {
                Iterations = [.. key.Iterations.Where(iteration => scope.CanRead(iteration.DefinitionId))],
            })
            .Where(key => key.Iterations.Count > 0)
            .ToArray();

        return new WorkIterationKeyQueryResult(keys, keys.Length, result.Skip, result.Take);
    }

    public async Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.WorkIterationKeyTypes(criteria, cancellationToken);
        var types = result.Types
            .Select(type =>
            {
                var iterations = type.Iterations.Where(iteration => scope.CanRead(iteration.DefinitionId)).ToArray();
                return type with
                {
                    IterationCount = iterations.Length,
                    IterationCountByKind = new Dictionary<WorkKeyKind, int>(),
                    Iterations = iterations,
                };
            })
            .Where(type => type.IterationCount > 0)
            .ToArray();

        return new WorkIterationKeyTypeQueryResult(types, types.Length, result.Skip, result.Take);
    }

    public async Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var workers = await this.ReadAllWorkers(criteria ?? new WorkerCriteria(), cancellationToken);
        return CreateWorkerStatusSummary(workers);
    }

    public async Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var innerDetails = await inner.SystemDetails(criteria, cancellationToken);
        var workerCounts = await this.SystemWorkerCounts(criteria, cancellationToken);
        var iterationCounts = await this.SystemIterationCounts(criteria, cancellationToken);
        var failedWorkers = await this.SystemFailedWorkers(criteria, cancellationToken);
        var failedIterations = await this.SystemFailedIterations(criteria, cancellationToken);
        var completedIterations = await this.SystemCompletedIterations(criteria, cancellationToken);

        return innerDetails with
        {
            DefinitionCount = workerCounts.DefinitionCount,
            ActiveWorkerCount = workerCounts.ActiveWorkerCount,
            FinalWorkerCount = workerCounts.FinalWorkerCount,
            FailedWorkerCount = workerCounts.FailedWorkerCount,
            WorkerCountByState = workerCounts.WorkerCountByState,
            OldestQueuedAt = workerCounts.OldestQueuedAt,
            CurrentIterationCount = iterationCounts.CurrentIterationCount,
            CompletedIterationCount = iterationCounts.CompletedIterationCount,
            FailedIterationCount = iterationCounts.FailedIterationCount,
            CanceledIterationCount = iterationCounts.CanceledIterationCount,
            IterationCountByStatus = iterationCounts.IterationCountByStatus,
            CommonKeyTypes = (await this.SystemCommonKeyTypes(criteria, cancellationToken)).KeyTypes,
            Throughput = await this.SystemThroughput(criteria, cancellationToken: cancellationToken),
            FailedWorkers = failedWorkers.FailedWorkers,
            FailedIterations = failedIterations.Iterations,
            CompletedIterations = completedIterations.Iterations,
        };
    }

    public Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
        => this.CanDelegateSystemAggregate(criteria)
            ? inner.SystemThroughput(criteria, throughput, cancellationToken)
            : Task.FromResult(CreateEmptyThroughput(throughput));

    public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default)
        => this.CanDelegateSystemAggregate(criteria)
            ? inner.SystemThroughputSummary(criteria, throughput, cancellationToken)
            : Task.FromResult(CreateEmptyThroughputSummary(throughput));

    public async Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var workers = await this.ReadAllWorkers(ToWorkerCriteria(criteria), cancellationToken);
        var byState = workers
            .GroupBy(worker => worker.State)
            .ToDictionary(group => group.Key, group => group.Count());
        var active = workers.Count(worker => !IsFinal(worker.State));
        var final = workers.Count - active;
        var failed = byState.GetValueOrDefault(WorkerState.Failed);
        var oldestQueued = workers
            .Where(worker => worker.State == WorkerState.Queued)
            .Select(worker => (DateTimeOffset?)worker.CreatedAt)
            .OrderBy(value => value)
            .FirstOrDefault();

        return new WorkSystemWorkerCounts(
            this.ReadableDefinitionCount(criteria),
            active,
            final,
            failed,
            byState,
            oldestQueued);
    }

    public async Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var iterations = await this.ReadAllIterations(ToIterationCriteria(criteria), cancellationToken);
        var byStatus = iterations
            .GroupBy(iteration => iteration.Status)
            .ToDictionary(group => group.Key, group => group.Count());

        return new WorkSystemIterationCounts(
            byStatus.Where(pair => pair.Key is not WorkCompletionStatus.Completed and not WorkCompletionStatus.Failed and not WorkCompletionStatus.Canceled)
                .Sum(pair => pair.Value),
            byStatus.GetValueOrDefault(WorkCompletionStatus.Completed),
            byStatus.GetValueOrDefault(WorkCompletionStatus.Failed),
            byStatus.GetValueOrDefault(WorkCompletionStatus.Canceled),
            byStatus);
    }

    public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => this.CanDelegateSystemAggregate(criteria)
            ? inner.SystemCommonKeyTypes(criteria, cancellationToken)
            : Task.FromResult(new WorkIterationKeyTypeFacetQueryResult([]));

    public async Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var workers = await this.ReadAllWorkers(ToWorkerCriteria(criteria) with
        {
            States = new HashSet<WorkerState> { WorkerState.Failed },
        }, cancellationToken);
        var counts = await this.SystemWorkerCounts(criteria, cancellationToken);
        return new WorkSystemFailedWorkers(
            counts.ActiveWorkerCount,
            counts.FinalWorkerCount,
            counts.FailedWorkerCount,
            counts.WorkerCountByState,
            [.. workers.Take(5)]);
    }

    public async Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var iterations = await this.ReadAllIterations(ToIterationCriteria(criteria) with
        {
            Statuses = new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Failed },
        }, cancellationToken);
        return new WorkerIterationOverviewQueryResult([.. iterations.Take(5)]);
    }

    public async Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var iterations = await this.ReadAllIterations(ToIterationCriteria(criteria) with
        {
            Statuses = new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed },
        }, cancellationToken);
        return new WorkerIterationOverviewQueryResult([.. iterations.Take(5)]);
    }

    private async Task<IReadOnlyList<WorkerOverviewItem>> ReadAllWorkers(
        WorkerCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (criteria.DefinitionId is { } definitionId && !scope.CanRead(definitionId))
        {
            return [];
        }

        var workers = new List<WorkerOverviewItem>();
        var skip = 0;
        while (true)
        {
            var page = await inner.Workers(criteria with
            {
                Skip = skip,
                Take = WorkerCriteria.MaximumTake,
            }, cancellationToken);
            if (page.Workers.Count == 0)
            {
                break;
            }

            workers.AddRange(page.Workers.Where(worker => scope.CanRead(worker.DefinitionId)));
            if (page.Workers.Count < WorkerCriteria.MaximumTake)
            {
                break;
            }

            skip += page.Workers.Count;
        }

        return workers;
    }

    private async Task<IReadOnlyList<WorkerIterationOverviewItem>> ReadAllIterations(
        WorkerIterationCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (criteria.DefinitionId is { } definitionId && !scope.CanRead(definitionId))
        {
            return [];
        }

        var iterations = new List<WorkerIterationOverviewItem>();
        var skip = 0;
        while (true)
        {
            var page = await inner.WorkerIterations(criteria with
            {
                Skip = skip,
                Take = WorkerIterationCriteria.MaximumTake,
            }, cancellationToken);
            if (page.Iterations.Count == 0)
            {
                break;
            }

            iterations.AddRange(page.Iterations.Where(iteration => scope.CanRead(iteration.DefinitionId)));
            if (page.Iterations.Count < WorkerIterationCriteria.MaximumTake)
            {
                break;
            }

            skip += page.Iterations.Count;
        }

        return iterations;
    }

    private bool CanDelegateSystemAggregate(WorkSystemCriteria? criteria)
        => this.AllDefinitionsReadable() ||
            (criteria?.DefinitionId is { } definitionId && scope.CanRead(definitionId));

    private bool AllDefinitionsReadable()
    {
        var definitionIds = catalog.Definitions.Select(definition => definition.Id).ToArray();
        return definitionIds.Length > 0 && definitionIds.All(scope.CanRead);
    }

    private int ReadableDefinitionCount(WorkSystemCriteria? criteria)
    {
        if (criteria?.DefinitionId is { } definitionId)
        {
            return scope.CanRead(definitionId) ? 1 : 0;
        }

        return catalog.Definitions.Count(definition => scope.CanRead(definition.Id));
    }

    private static WorkerStatusSummary CreateWorkerStatusSummary(IReadOnlyList<WorkerOverviewItem> workers)
    {
        var counts = workers
            .GroupBy(worker => worker.State)
            .ToDictionary(group => group.Key, group => group.Count());
        var active = workers.Count(worker => !IsFinal(worker.State));
        var final = workers.Count - active;
        return new WorkerStatusSummary(workers.Count, active, final, counts);
    }

    private static WorkerCriteria ToWorkerCriteria(WorkSystemCriteria? criteria)
        => new(
            DefinitionId: criteria?.DefinitionId,
            DefinitionName: criteria?.DefinitionName,
            Category: criteria?.Category,
            IncludeSubcategories: criteria?.IncludeSubcategories ?? true,
            Take: WorkerCriteria.MaximumTake);

    private static WorkerIterationCriteria ToIterationCriteria(WorkSystemCriteria? criteria)
        => new(
            DefinitionId: criteria?.DefinitionId,
            DefinitionName: criteria?.DefinitionName,
            Category: criteria?.Category,
            Take: WorkerIterationCriteria.MaximumTake);

    private static int NormalizeTake(int take, int defaultTake, int maximumTake)
        => take <= 0 ? defaultTake : Math.Min(take, maximumTake);

    private static bool IsFinal(WorkerState state)
        => state is WorkerState.Completed or WorkerState.Failed or WorkerState.Canceled;

    private static WorkSystemThroughput CreateEmptyThroughput(WorkThroughputCriteria? throughput)
    {
        throughput ??= new WorkThroughputCriteria();
        var now = DateTimeOffset.UtcNow;
        return new WorkSystemThroughput(
            now.AddSeconds(-throughput.WindowSeconds),
            now,
            throughput.WindowSeconds,
            throughput.BucketSeconds,
            0,
            [],
            new WorkThroughputExecutionSummary(0, 0, 0, 0, 0),
            new WorkThroughputLiveSummary(throughput.WindowSeconds, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    private static WorkSystemThroughputSummary CreateEmptyThroughputSummary(WorkThroughputCriteria? throughput)
    {
        throughput ??= new WorkThroughputCriteria();
        return new WorkSystemThroughputSummary(
            throughput.WindowSeconds,
            0,
            new WorkThroughputExecutionSummary(0, 0, 0, 0, 0),
            new WorkThroughputLiveSummary(throughput.WindowSeconds, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }
}

using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkQueryServiceShould
{
    [Fact]
    public async Task ScopeCollectionQueriesToReadableDefinitions()
    {
        var query = CreateQueryService(
            out var visible,
            out _,
            out var inner);

        await query.WorkDefinitions();
        await query.Workers();
        await query.WorkerIterations();
        await query.WorkerKeys();
        await query.WorkerKeyTypes();
        await query.WorkIterationKeys();
        await query.WorkIterationKeyTypes();
        await query.WorkerStatusSummary();
        await query.SystemDetails();
        await query.SystemThroughput();
        await query.SystemThroughputSummary();
        await query.SystemWorkerCounts();
        await query.SystemIterationCounts();
        await query.SystemCommonKeyTypes();
        await query.SystemFailedWorkers();
        await query.SystemFailedIterations();
        await query.SystemCompletedIterations();

        AssertOnlyDefinition(visible.Id, inner.LastWorkDefinitionsCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkersCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkerIterationsCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkerKeysCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkerKeyTypesCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkIterationKeysCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkIterationKeyTypesCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastWorkerStatusSummaryCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemDetailsCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemThroughputCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemThroughputSummaryCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemWorkerCountsCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemIterationCountsCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemCommonKeyTypesCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemFailedWorkersCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemFailedIterationsCriteria?.DefinitionIds);
        AssertOnlyDefinition(visible.Id, inner.LastSystemCompletedIterationsCriteria?.DefinitionIds);
    }

    [Fact]
    public async Task ReturnEmptyResultsWithoutCallingInnerWhenRequestedDefinitionIsUnreadable()
    {
        var query = CreateQueryService(
            out _,
            out var hidden,
            out var inner);
        var hiddenDefinitions = new HashSet<WorkDefinitionId> { hidden.Id };

        var definitions = await query.WorkDefinitions(new WorkDefinitionCriteria(Id: hidden.Id));
        var workers = await query.Workers(new WorkerCriteria(
            DefinitionIds: hiddenDefinitions,
            Skip: -1,
            Take: WorkerCriteria.MaximumTake + 10));
        var iterations = await query.WorkerIterations(new WorkerIterationCriteria(
            DefinitionIds: hiddenDefinitions,
            Skip: -1,
            Take: WorkerIterationCriteria.MaximumTake + 10));
        var workerKeys = await query.WorkerKeys(new WorkerKeyCriteria(
            DefinitionIds: hiddenDefinitions,
            Skip: -1,
            Take: WorkerKeyCriteria.MaximumTake + 10));
        var workerKeyTypes = await query.WorkerKeyTypes(new WorkerKeyTypeCriteria(
            DefinitionIds: hiddenDefinitions,
            Skip: -1,
            Take: WorkerKeyCriteria.MaximumTake + 10));
        var iterationKeys = await query.WorkIterationKeys(new WorkIterationKeyCriteria(
            DefinitionIds: hiddenDefinitions,
            Skip: -1,
            Take: WorkIterationKeyCriteria.MaximumTake + 10));
        var iterationKeyTypes = await query.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(
            DefinitionIds: hiddenDefinitions,
            Skip: -1,
            Take: WorkIterationKeyCriteria.MaximumTake + 10));
        var status = await query.WorkerStatusSummary(new WorkerCriteria(DefinitionIds: hiddenDefinitions));

        Assert.Empty(definitions.Definitions);
        Assert.Empty(workers.Workers);
        Assert.Equal(0, workers.Skip);
        Assert.Equal(WorkerCriteria.MaximumTake, workers.Take);
        Assert.Empty(iterations.Iterations);
        Assert.Equal(0, iterations.Skip);
        Assert.Equal(WorkerIterationCriteria.MaximumTake, iterations.Take);
        Assert.Empty(workerKeys.Keys);
        Assert.Equal(0, workerKeys.Skip);
        Assert.Equal(WorkerKeyCriteria.MaximumTake, workerKeys.Take);
        Assert.Empty(workerKeyTypes.Types);
        Assert.Equal(0, workerKeyTypes.Skip);
        Assert.Equal(WorkerKeyCriteria.MaximumTake, workerKeyTypes.Take);
        Assert.Empty(iterationKeys.Keys);
        Assert.Equal(0, iterationKeys.Skip);
        Assert.Equal(WorkIterationKeyCriteria.MaximumTake, iterationKeys.Take);
        Assert.Empty(iterationKeyTypes.Types);
        Assert.Equal(0, iterationKeyTypes.Skip);
        Assert.Equal(WorkIterationKeyCriteria.MaximumTake, iterationKeyTypes.Take);
        Assert.Equal(0, status.Total);
        Assert.Equal(0, inner.CollectionQueryCallCount);
    }

    [Fact]
    public async Task HideSingleWorkerIterationAndWorkInfoOutsideReadableDefinitions()
    {
        var query = CreateQueryService(
            out var visible,
            out var hidden,
            out var inner);
        var workerId = WorkerId.New();
        inner.WorkerToReturn = CreateWorker(workerId, hidden);
        inner.WorkInfoToReturn = CreateWorkInfo(visible);
        inner.WorkerIterationToReturn = CreateIteration();

        Assert.Null(await query.Worker(workerId));
        Assert.Null(await query.WorkerIteration(new WorkerIterationReference(workerId, 1)));
        Assert.Null(await query.WorkInfo(hidden.Id));
        Assert.Null(await query.WorkInfo(hidden.Name));

        Assert.Equal(2, inner.WorkerCallCount);
        Assert.Equal(0, inner.WorkerIterationCallCount);
        Assert.Equal(0, inner.WorkInfoByIdCallCount);
        Assert.Equal(0, inner.WorkInfoByNameCallCount);

        inner.WorkerToReturn = CreateWorker(workerId, visible);

        Assert.NotNull(await query.Worker(workerId));
        Assert.NotNull(await query.WorkerIteration(new WorkerIterationReference(workerId, 1)));
        Assert.NotNull(await query.WorkInfo(visible.Id));
        Assert.NotNull(await query.WorkInfo(visible.Name));

        Assert.Equal(4, inner.WorkerCallCount);
        Assert.Equal(1, inner.WorkerIterationCallCount);
        Assert.Equal(1, inner.WorkInfoByIdCallCount);
        Assert.Equal(1, inner.WorkInfoByNameCallCount);
    }

    [Fact]
    public async Task PassAnEmptySystemScopeWhenRequestedDefinitionIsUnreadable()
    {
        var query = CreateQueryService(
            out _,
            out var hidden,
            out var inner);

        await query.SystemDetails(new WorkSystemCriteria(DefinitionId: hidden.Id));

        var criteria = inner.LastSystemDetailsCriteria;
        Assert.NotNull(criteria);
        var definitionIds = criteria!.DefinitionIds;
        Assert.NotNull(definitionIds);
        Assert.Empty(definitionIds);
    }

    private static AuthorizedWorkQueryService CreateQueryService(
        out WorkDefinition visible,
        out WorkDefinition hidden,
        out RecordingWorkQueryService inner)
    {
        visible = CreateDefinition("visible.work", "visible.read");
        hidden = CreateDefinition("hidden.work", "hidden.read");
        var catalog = new WorkSystemCatalog(
            [
                CreateRegisteredWork(visible),
                CreateRegisteredWork(hidden),
            ],
            persistenceStoreAvailable: false);
        inner = new RecordingWorkQueryService();
        return new AuthorizedWorkQueryService(
            catalog,
            inner,
            new WorkAuthorizationEvaluator(catalog, Groups("visible.read"), false));
    }

    private static WorkDefinition CreateDefinition(string name, string readGroup)
        => WorkDefinition.Create(
            name,
            authorization: WorkDefinitionAuthorization.Create(readGroups: [readGroup]));

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static WorkerSnapshot CreateWorker(WorkerId workerId, WorkDefinition definition)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerSnapshot(
            workerId,
            Revision: 1,
            StateSequence: 1,
            definition.Id,
            definition.Name,
            definition.Category,
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            Input: null,
            Output: null,
            WorkerOptions.Default,
            definition.Configuration,
            Messages: [],
            InterruptionReason: null,
            CreatedAt: now,
            StateChangedAt: now,
            UpdatedAt: now);
    }

    private static WorkerIterationSnapshot CreateIteration()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerIterationSnapshot(
            Sequence: 1,
            StartedAt: now,
            CompletedAt: now,
            ExecutionDuration: TimeSpan.Zero,
            WorkCompletionStatus.Completed,
            Output: null,
            Messages: []);
    }

    private static WorkInfo CreateWorkInfo(WorkDefinition definition)
        => new(
            definition,
            WorkDefinitionStatus.Healthy,
            new WorkerRollup(0, 0, 0, 0, 0, 0, 0, 0, 0, LastActivityAt: null));

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

    private static void AssertOnlyDefinition(
        WorkDefinitionId expected,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        Assert.NotNull(definitionIds);
        Assert.Equal(expected, Assert.Single(definitionIds));
    }

    private sealed class RecordingWorkQueryService : IWorkQueryService
    {
        private static readonly WorkThroughputExecutionSummary EmptyExecutionSummary = new(
            ExecutionCount: 0,
            AverageExecutionMilliseconds: 0,
            SlowestExecutionMilliseconds: 0,
            P95ExecutionMilliseconds: 0,
            P99ExecutionMilliseconds: 0);

        private static readonly WorkThroughputLiveSummary EmptyLiveSummary = new(
            WindowSeconds: 0,
            StartedPerSecond: 0,
            CompletedPerSecond: 0,
            FailedPerSecond: 0,
            CanceledPerSecond: 0,
            InFlightDeltaPerSecond: 0,
            AverageExecutionMilliseconds: 0,
            ExecutionCount: 0,
            SlowestExecutionMilliseconds: 0,
            P95ExecutionMilliseconds: 0,
            P99ExecutionMilliseconds: 0);

        public WorkerSnapshot? WorkerToReturn { get; set; }

        public WorkerIterationSnapshot? WorkerIterationToReturn { get; set; }

        public WorkInfo? WorkInfoToReturn { get; set; }

        public int WorkerCallCount { get; private set; }

        public int WorkerIterationCallCount { get; private set; }

        public int WorkInfoByIdCallCount { get; private set; }

        public int WorkInfoByNameCallCount { get; private set; }

        public int CollectionQueryCallCount { get; private set; }

        public WorkDefinitionCriteria? LastWorkDefinitionsCriteria { get; private set; }

        public WorkerCriteria? LastWorkersCriteria { get; private set; }

        public WorkerIterationCriteria? LastWorkerIterationsCriteria { get; private set; }

        public WorkerKeyCriteria? LastWorkerKeysCriteria { get; private set; }

        public WorkerKeyTypeCriteria? LastWorkerKeyTypesCriteria { get; private set; }

        public WorkIterationKeyCriteria? LastWorkIterationKeysCriteria { get; private set; }

        public WorkIterationKeyTypeCriteria? LastWorkIterationKeyTypesCriteria { get; private set; }

        public WorkerCriteria? LastWorkerStatusSummaryCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemDetailsCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemThroughputCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemThroughputSummaryCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemWorkerCountsCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemIterationCountsCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemCommonKeyTypesCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemFailedWorkersCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemFailedIterationsCriteria { get; private set; }

        public WorkSystemCriteria? LastSystemCompletedIterationsCriteria { get; private set; }

        public Task<WorkerSnapshot?> Worker(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
        {
            this.WorkerCallCount++;
            return Task.FromResult(this.WorkerToReturn);
        }

        public Task<WorkerIterationSnapshot?> WorkerIteration(
            WorkerIterationReference iteration,
            CancellationToken cancellationToken = default)
        {
            this.WorkerIterationCallCount++;
            return Task.FromResult(this.WorkerIterationToReturn);
        }

        public Task<WorkerQueryResult> Workers(
            WorkerCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkersCriteria = criteria;
            return Task.FromResult(new WorkerQueryResult([], 0, criteria?.Skip ?? 0, criteria?.Take ?? 0));
        }

        public Task<WorkerIterationQueryResult> WorkerIterations(
            WorkerIterationCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkerIterationsCriteria = criteria;
            return Task.FromResult(new WorkerIterationQueryResult([], 0, criteria?.Skip ?? 0, criteria?.Take ?? 0));
        }

        public Task<WorkInfo?> WorkInfo(
            WorkDefinitionId definitionId,
            CancellationToken cancellationToken = default)
        {
            this.WorkInfoByIdCallCount++;
            return Task.FromResult(this.WorkInfoToReturn);
        }

        public Task<WorkInfo?> WorkInfo(
            string name,
            CancellationToken cancellationToken = default)
        {
            this.WorkInfoByNameCallCount++;
            return Task.FromResult(this.WorkInfoToReturn);
        }

        public Task<WorkDefinitionQueryResult> WorkDefinitions(
            WorkDefinitionCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkDefinitionsCriteria = criteria;
            return Task.FromResult(new WorkDefinitionQueryResult([]));
        }

        public Task<WorkerKeyQueryResult> WorkerKeys(
            WorkerKeyCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkerKeysCriteria = criteria;
            return Task.FromResult(new WorkerKeyQueryResult([], 0, criteria?.Skip ?? 0, criteria?.Take ?? 0));
        }

        public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
            WorkerKeyTypeCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkerKeyTypesCriteria = criteria;
            return Task.FromResult(new WorkerKeyTypeQueryResult([], 0, criteria?.Skip ?? 0, criteria?.Take ?? 0));
        }

        public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
            WorkIterationKeyCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkIterationKeysCriteria = criteria;
            return Task.FromResult(new WorkIterationKeyQueryResult([], 0, criteria?.Skip ?? 0, criteria?.Take ?? 0));
        }

        public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
            WorkIterationKeyTypeCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkIterationKeyTypesCriteria = criteria;
            return Task.FromResult(new WorkIterationKeyTypeQueryResult([], 0, criteria?.Skip ?? 0, criteria?.Take ?? 0));
        }

        public Task<WorkerStatusSummary> WorkerStatusSummary(
            WorkerCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastWorkerStatusSummaryCriteria = criteria;
            return Task.FromResult(new WorkerStatusSummary(0, 0, 0, new Dictionary<WorkerState, int>()));
        }

        public Task<WorkSystemDetails> SystemDetails(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemDetailsCriteria = criteria;
            return Task.FromResult(CreateSystemDetails());
        }

        public Task<WorkSystemThroughput> SystemThroughput(
            WorkSystemCriteria? criteria = null,
            WorkThroughputCriteria? throughput = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemThroughputCriteria = criteria;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new WorkSystemThroughput(
                now,
                now,
                WindowSeconds: 0,
                BucketSeconds: 0,
                SettledCount: 0,
                Buckets: [],
                EmptyExecutionSummary,
                EmptyLiveSummary));
        }

        public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
            WorkSystemCriteria? criteria = null,
            WorkThroughputCriteria? throughput = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemThroughputSummaryCriteria = criteria;
            return Task.FromResult(new WorkSystemThroughputSummary(
                WindowSeconds: 0,
                SettledCount: 0,
                EmptyExecutionSummary,
                EmptyLiveSummary));
        }

        public Task<WorkSystemWorkerCounts> SystemWorkerCounts(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemWorkerCountsCriteria = criteria;
            return Task.FromResult(new WorkSystemWorkerCounts(
                0,
                0,
                0,
                0,
                new Dictionary<WorkerState, int>(),
                OldestQueuedAt: null));
        }

        public Task<WorkSystemIterationCounts> SystemIterationCounts(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemIterationCountsCriteria = criteria;
            return Task.FromResult(new WorkSystemIterationCounts(
                0,
                0,
                0,
                0,
                new Dictionary<WorkCompletionStatus, int>()));
        }

        public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemCommonKeyTypesCriteria = criteria;
            return Task.FromResult(new WorkIterationKeyTypeFacetQueryResult([]));
        }

        public Task<WorkSystemFailedWorkers> SystemFailedWorkers(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemFailedWorkersCriteria = criteria;
            return Task.FromResult(new WorkSystemFailedWorkers(
                0,
                0,
                0,
                new Dictionary<WorkerState, int>(),
                FailedWorkers: []));
        }

        public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemFailedIterationsCriteria = criteria;
            return Task.FromResult(new WorkerIterationOverviewQueryResult([]));
        }

        public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.CollectionQueryCallCount++;
            this.LastSystemCompletedIterationsCriteria = criteria;
            return Task.FromResult(new WorkerIterationOverviewQueryResult([]));
        }

        private static WorkSystemDetails CreateSystemDetails()
            => new(
                SystemName: null,
                WorkSystemState.Started,
                DefinitionCount: 0,
                ActiveWorkerCount: 0,
                FinalWorkerCount: 0,
                FailedWorkerCount: 0,
                WorkerCountByState: new Dictionary<WorkerState, int>(),
                OldestQueuedAt: null,
                CurrentIterationCount: 0,
                CompletedIterationCount: 0,
                FailedIterationCount: 0,
                CanceledIterationCount: 0,
                IterationCountByStatus: new Dictionary<WorkCompletionStatus, int>(),
                CommonKeyTypes: [],
                Throughput: null,
                FailedWorkers: [],
                FailedIterations: [],
                CompletedIterations: []);
    }

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}

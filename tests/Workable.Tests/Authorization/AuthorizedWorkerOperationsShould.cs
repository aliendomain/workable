using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkerOperationsShould
{
    [Fact]
    public async Task ReturnUnauthorizedWithoutCallingInnerForWorkersOutsideOperateScope()
    {
        var operations = CreateOperations(
            groups: ["visible.operate"],
            out _,
            out var hidden,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkerToReturn = CreateWorkerSnapshot(workerId, hidden);

        var outcome = await operations.Execute(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
        Assert.Equal(WorkAction.Cancel, outcome.Action);
        Assert.Equal(workerId, outcome.WorkerId);
        Assert.Equal(1, query.WorkerCallCount);
        Assert.Empty(inner.Executed);
    }

    [Fact]
    public async Task ReturnEmptyBulkOutcomeWithoutQueryingWhenNoDefinitionsAreOperable()
    {
        var operations = CreateOperations(
            groups: [],
            out _,
            out _,
            out var query,
            out var inner);
        var filter = new WorkerBulkActionFilter("Billing", IncludeSubcategories: false);

        var outcome = await operations.ExecuteAll(WorkAction.Pause, filter);

        Assert.Equal(WorkAction.Pause, outcome.Action);
        Assert.Equal(filter, outcome.Filter);
        Assert.Equal(0, outcome.MatchedWorkerCount);
        Assert.Empty(outcome.Outcomes);
        Assert.Equal(0, query.WorkersCallCount);
        Assert.Empty(inner.Executed);
    }

    [Fact]
    public async Task ScopeBulkActionsToOperableDefinitionsAndForwardWorkerVersions()
    {
        var operations = CreateOperations(
            groups: ["visible.operate"],
            out var visible,
            out _,
            out var query,
            out var inner);
        var first = WorkerId.New();
        var second = WorkerId.New();
        query.WorkerPages.Enqueue([
            CreateWorkerOverview(first, visible, revision: 3),
            CreateWorkerOverview(second, visible, revision: 5),
        ]);
        var filter = new WorkerBulkActionFilter("Operations", IncludeSubcategories: false);

        var outcome = await operations.ExecuteAll(WorkAction.Cancel, filter);

        Assert.Equal(2, outcome.MatchedWorkerCount);
        Assert.Equal(2, outcome.Outcomes.Count);
        Assert.Equal(1, query.WorkersCallCount);
        var criteria = query.LastWorkersCriteria ?? throw new InvalidOperationException("Expected worker query criteria.");
        Assert.Equal("Operations", criteria.Category);
        Assert.False(criteria.IncludeSubcategories);
        Assert.Equal(0, criteria.Skip);
        Assert.Equal(WorkerCriteria.MaximumTake, criteria.Take);
        var definitionIds = criteria.DefinitionIds
            ?? throw new InvalidOperationException("Expected scoped definition ids.");
        Assert.Equal(visible.Id, Assert.Single(definitionIds));
        Assert.Equal([
            new RecordedAction(new WorkerVersion(first, Revision: 3), WorkAction.Cancel),
            new RecordedAction(new WorkerVersion(second, Revision: 5), WorkAction.Cancel),
        ], inner.Executed);
    }

    private static AuthorizedWorkerOperations CreateOperations(
        IReadOnlyList<string> groups,
        out WorkDefinition visible,
        out WorkDefinition hidden,
        out RecordingWorkQueryService query,
        out RecordingWorkerOperations inner)
    {
        visible = CreateDefinition("visible.work", "visible.operate");
        hidden = CreateDefinition("hidden.work", "hidden.operate");
        var catalog = new WorkSystemCatalog(
            [
                CreateRegisteredWork(visible),
                CreateRegisteredWork(hidden),
            ],
            persistenceStoreAvailable: false);
        query = new RecordingWorkQueryService();
        inner = new RecordingWorkerOperations();
        return new AuthorizedWorkerOperations(
            inner,
            query,
            new WorkAuthorizationEvaluator(catalog, Groups(groups), false));
    }

    private static WorkDefinition CreateDefinition(string name, string operateGroup)
        => WorkDefinition.Create(
            name,
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: [operateGroup],
                operateGroups: [operateGroup]));

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static WorkerSnapshot CreateWorkerSnapshot(WorkerId workerId, WorkDefinition definition, long revision = 1)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerSnapshot(
            workerId,
            revision,
            StateSequence: 1,
            definition.Id,
            definition.Name,
            definition.Category,
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.DotNet),
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

    private static WorkerOverviewItem CreateWorkerOverview(
        WorkerId workerId,
        WorkDefinition definition,
        long revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerOverviewItem(
            workerId,
            definition.Id,
            definition.Name,
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            revision,
            definition.Category,
            WorkerState.Queued,
            InterruptionReason: null,
            now,
            now,
            now);
    }

    private static IReadOnlySet<string> Groups(IEnumerable<string> groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record RecordedAction(WorkerVersion Worker, WorkAction Action);

    private sealed class RecordingWorkerOperations : IWorkerOperations
    {
        public List<RecordedAction> Executed { get; } = [];

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkAction action,
            CancellationToken cancellationToken = default)
        {
            this.Executed.Add(new RecordedAction(worker, action));
            return Task.FromResult(WorkActionOutcome.NotFound(action, worker.WorkerId));
        }

        public Task<WorkerBulkActionOutcome> ExecuteAll(
            WorkAction action,
            WorkerBulkActionFilter? filter = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkActionOutcome> Reconfigure(
            WorkerVersion worker,
            WorkerReconfiguration changes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingWorkQueryService : IWorkQueryService
    {
        public WorkerSnapshot? WorkerToReturn { get; set; }

        public Queue<IReadOnlyList<WorkerOverviewItem>> WorkerPages { get; } = [];

        public int WorkerCallCount { get; private set; }

        public int WorkersCallCount { get; private set; }

        public WorkerCriteria? LastWorkersCriteria { get; private set; }

        public Task<WorkerSnapshot?> Worker(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
        {
            this.WorkerCallCount++;
            return Task.FromResult(this.WorkerToReturn);
        }

        public Task<WorkerQueryResult> Workers(
            WorkerCriteria? criteria = null,
            CancellationToken cancellationToken = default)
        {
            this.WorkersCallCount++;
            this.LastWorkersCriteria = criteria;
            var workers = this.WorkerPages.Count > 0 ? this.WorkerPages.Dequeue() : [];
            return Task.FromResult(new WorkerQueryResult(
                workers,
                workers.Count,
                criteria?.Skip ?? 0,
                criteria?.Take ?? 0));
        }

        public Task<WorkerIterationSnapshot?> WorkerIteration(
            WorkerIterationReference iteration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerIterationQueryResult> WorkerIterations(
            WorkerIterationCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkInfo?> WorkInfo(
            WorkDefinitionId definitionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkInfo?> WorkInfo(
            string name,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkDefinitionQueryResult> WorkDefinitions(
            WorkDefinitionCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerKeyQueryResult> WorkerKeys(
            WorkerKeyCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
            WorkerKeyTypeCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
            WorkIterationKeyCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
            WorkIterationKeyTypeCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerStatusSummary> WorkerStatusSummary(
            WorkerCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemDetails> SystemDetails(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemThroughput> SystemThroughput(
            WorkSystemCriteria? criteria = null,
            WorkThroughputCriteria? throughput = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
            WorkSystemCriteria? criteria = null,
            WorkThroughputCriteria? throughput = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemWorkerCounts> SystemWorkerCounts(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemIterationCounts> SystemIterationCounts(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemFailedWorkers> SystemFailedWorkers(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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

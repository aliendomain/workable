using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkerOperationsShould
{
    [Fact]
    public async Task ForwardAuthorizedWorkerActionRequestWithReason()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(workerId, visible.Definition);
        var request = new WorkerActionRequest(WorkAction.Cancel, "The customer withdrew the order.");

        await operations.Execute(new WorkerVersion(workerId, Revision: 7), request);

        Assert.Equal(request, Assert.Single(inner.ExecutedRequests));
        Assert.Equal(
            new RecordedAction(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel),
            Assert.Single(inner.Executed));
    }

    [Fact]
    public async Task ReturnUnauthorizedWithoutCallingInnerForWorkersOutsideOperateScope()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var hidden = CreateRegisteredWork("hidden.work", authorize => authorize.AllowOperateToGroups("hidden.operate"));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible, hidden],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(workerId, hidden.Definition);

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
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var operations = CreateOperations(
            groups: [],
            works: [visible],
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
    public async Task ReturnEmptyBulkOutcomeWithoutQueryingWhenCallerOnlyHasDifferentOperationAcrossAllDefinitions()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowQueueToGroups("visible.queue"));
        var operations = CreateOperations(
            groups: ["visible.queue"],
            works: [visible],
            out _,
            out var query,
            out var inner);

        var outcome = await operations.ExecuteAll(WorkAction.Pause);

        Assert.Equal(WorkAction.Pause, outcome.Action);
        Assert.Equal(0, outcome.MatchedWorkerCount);
        Assert.Empty(outcome.Outcomes);
        Assert.Equal(0, query.WorkersCallCount);
        Assert.Empty(inner.Executed);
    }

    [Fact]
    public async Task ScopeBulkActionsToOperableDefinitionsAndForwardWorkerVersions()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var hidden = CreateRegisteredWork("hidden.work", authorize => authorize.AllowOperateToGroups("hidden.operate"));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible, hidden],
            out _,
            out var query,
            out var inner);
        var first = WorkerId.New();
        var second = WorkerId.New();
        query.WorkerPages.Enqueue([
            CreateWorkerOverview(first, visible.Definition, revision: 3),
            CreateWorkerOverview(second, visible.Definition, revision: 5),
        ]);
        query.WorkersById[first] = CreateWorkerSnapshot(first, visible.Definition, revision: 3);
        query.WorkersById[second] = CreateWorkerSnapshot(second, visible.Definition, revision: 5);
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
        var definitionNames = criteria.DefinitionNames
            ?? throw new InvalidOperationException("Expected scoped definition names.");
        Assert.Equal(visible.Definition.Name, Assert.Single(definitionNames));
        Assert.Equal([
            new RecordedAction(new WorkerVersion(first, Revision: 3), WorkAction.Cancel),
            new RecordedAction(new WorkerVersion(second, Revision: 5), WorkAction.Cancel),
        ], inner.Executed);
    }

    [Theory]
    [InlineData(WorkAction.Start, true)]
    [InlineData(WorkAction.Start, false)]
    [InlineData(WorkAction.Pause, true)]
    [InlineData(WorkAction.Pause, false)]
    [InlineData(WorkAction.Cancel, true)]
    [InlineData(WorkAction.Cancel, false)]
    [InlineData(WorkAction.Push, true)]
    [InlineData(WorkAction.Push, false)]
    [InlineData(WorkAction.Purge, true)]
    [InlineData(WorkAction.Purge, false)]
    public async Task ApplyCommonOperateRequirementsToEveryWorkerAction(WorkAction action, bool allow)
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenOperatingRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput(allow ? "allowed" : "denied"), WorkData.DefaultJsonOptions));

        var outcome = await operations.Execute(new WorkerVersion(workerId, Revision: 7), action);

        Assert.Equal(action, outcome.Action);
        if (allow)
        {
            Assert.Single(inner.Executed);
            Assert.Equal(new WorkerVersion(workerId, Revision: 7), inner.Executed[0].Worker);
            Assert.Equal(action, inner.Executed[0].Action);
        }
        else
        {
            Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
            Assert.Empty(inner.Executed);
        }
    }

    [Fact]
    public async Task DoNotApplyQueueOnlyRequirementsToWorkerActions()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenQueueingRequire<QueueInput>(_ => false)));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput("denied"), WorkData.DefaultJsonOptions));

        await operations.Execute(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel);

        Assert.Single(inner.Executed);
    }

    [Fact]
    public async Task DoNotTreatQueueOnlyGrantsAsWorkerActionPermission()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowQueueToGroups("visible.queue"));
        var operations = CreateOperations(
            groups: ["visible.queue"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(workerId, visible.Definition);

        var outcome = await operations.Execute(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
        Assert.Empty(inner.Executed);
    }

    [Theory]
    [InlineData(WorkAction.Start, true)]
    [InlineData(WorkAction.Cancel, false)]
    public async Task HonorSpecificWorkerActionMasks(WorkAction action, bool shouldAllow)
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperationsToGroups(
                ["visible.operate"],
                WorkOperationPermissions.Start));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(workerId, visible.Definition);

        var outcome = await operations.Execute(new WorkerVersion(workerId, Revision: 7), action);

        Assert.Equal(action, outcome.Action);
        if (shouldAllow)
        {
            Assert.Single(inner.Executed);
            Assert.Equal(action, inner.Executed[0].Action);
        }
        else
        {
            Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
            Assert.Empty(inner.Executed);
        }
    }

    [Fact]
    public async Task UsePersistedOriginalInputForWorkerActionRequirements()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenWorkerActionsRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput("allowed"), WorkData.DefaultJsonOptions));

        await operations.Execute(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel);

        Assert.Single(inner.Executed);
    }

    [Fact]
    public async Task ReturnInvalidWhenTypedWorkerActionRequirementCannotDeserializeInput()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenWorkerActionsRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromJson("\"not-an-object\""));

        var outcome = await operations.Execute(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message => message.Code == "workable.authorization.operate_requirement_input_invalid");
        Assert.Empty(inner.Executed);
    }

    [Fact]
    public async Task ReturnUnauthorizedBulkOutcomesForConstrainedWorkers()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenWorkerActionsRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkerPages.Enqueue([
            CreateWorkerOverview(workerId, visible.Definition, revision: 3),
        ]);
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            revision: 3,
            input: WorkInput.FromValue(new QueueInput("denied"), WorkData.DefaultJsonOptions));

        var outcome = await operations.ExecuteAll(WorkAction.Cancel);

        Assert.Equal(1, outcome.MatchedWorkerCount);
        Assert.Single(outcome.Outcomes);
        Assert.Equal(WorkActionStatus.Unauthorized, outcome.Outcomes[0].Status);
        Assert.Empty(inner.Executed);
    }

    [Fact]
    public async Task BypassConstrainedWorkerActionRequirementsForWorkAdministrators()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenWorkerActionsRequire<QueueInput>(_ => false)));
        var operations = CreateOperations(
            groups: ["work.admin"],
            works: [visible],
            out _,
            out var query,
            out var inner,
            systemAuthorizationConfiguration: WorkSystemAuthorizationConfiguration.Default with
            {
                WorkAdministratorGroups = Groups(["work.admin"]),
            });
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput("denied"), WorkData.DefaultJsonOptions));

        await operations.Execute(new WorkerVersion(workerId, Revision: 7), WorkAction.Cancel);

        Assert.Single(inner.Executed);
    }

    [Fact]
    public async Task ApplyWorkerReconfigurationRequirementsToReconfigure()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperationsToGroups(
                ["visible.operate"],
                WorkOperationPermissions.ReconfigureWorker,
                operate => operate.WhenWorkerReconfiguringRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var allowedWorkerId = WorkerId.New();
        query.WorkersById[allowedWorkerId] = CreateWorkerSnapshot(
            allowedWorkerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput("allowed"), WorkData.DefaultJsonOptions));
        var deniedWorkerId = WorkerId.New();
        query.WorkersById[deniedWorkerId] = CreateWorkerSnapshot(
            deniedWorkerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput("denied"), WorkData.DefaultJsonOptions));

        var accepted = await operations.Reconfigure(
            new WorkerVersion(allowedWorkerId, Revision: 7),
            new WorkerReconfiguration(ProfilingEnabled: true));
        var rejected = await operations.Reconfigure(
            new WorkerVersion(deniedWorkerId, Revision: 8),
            new WorkerReconfiguration(ProfilingEnabled: true));

        Assert.Equal(WorkActionStatus.Unauthorized, rejected.Status);
        Assert.Single(inner.Reconfigured);
        Assert.Equal(allowedWorkerId, inner.Reconfigured[0].Worker.WorkerId);
        Assert.Equal(WorkAction.Start, accepted.Action);
    }

    [Fact]
    public async Task DoNotApplyWorkerActionRequirementsToReconfigure()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperationsToGroups(
                ["visible.operate"],
                WorkOperationPermissions.ReconfigureWorker,
                operate => operate.WhenWorkerActionsRequire<QueueInput>(_ => false)));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromValue(new QueueInput("denied"), WorkData.DefaultJsonOptions));

        await operations.Reconfigure(
            new WorkerVersion(workerId, Revision: 7),
            new WorkerReconfiguration(ProfilingEnabled: true));

        Assert.Single(inner.Reconfigured);
    }

    [Fact]
    public async Task ReturnInvalidWhenTypedWorkerReconfigurationRequirementCannotDeserializeInput()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperationsToGroups(
                ["visible.operate"],
                WorkOperationPermissions.ReconfigureWorker,
                operate => operate.WhenWorkerReconfiguringRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var operations = CreateOperations(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var query,
            out var inner);
        var workerId = WorkerId.New();
        query.WorkersById[workerId] = CreateWorkerSnapshot(
            workerId,
            visible.Definition,
            input: WorkInput.FromJson("\"not-an-object\""));

        var outcome = await operations.Reconfigure(
            new WorkerVersion(workerId, Revision: 7),
            new WorkerReconfiguration(ProfilingEnabled: true));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message => message.Code == "workable.authorization.operate_requirement_input_invalid");
        Assert.Empty(inner.Reconfigured);
    }

    private static AuthorizedWorkerOperations CreateOperations(
        IReadOnlyList<string> groups,
        IReadOnlyList<RegisteredWork> works,
        out WorkSystemCatalog catalog,
        out RecordingWorkQueryService query,
        out RecordingWorkerOperations inner,
        bool isKnownAuthenticatedUser = false,
        WorkSystemAuthorizationConfiguration? systemAuthorizationConfiguration = null)
    {
        catalog = new WorkSystemCatalog(works, persistenceStoreAvailable: false);
        query = new RecordingWorkQueryService();
        inner = new RecordingWorkerOperations();
        var requestContext = CreateRequestContext(isKnownAuthenticatedUser);
        var normalizedGroups = Groups(groups);
        return new AuthorizedWorkerOperations(
            catalog,
            inner,
            query,
            new WorkAuthorizationEvaluator(
                catalog,
                normalizedGroups,
                isKnownAuthenticatedUser,
                systemAuthorizationConfiguration is null
                    ? null
                    : new WorkSystemAuthorizationEvaluator(systemAuthorizationConfiguration, normalizedGroups)),
            requestContext);
    }

    private static RegisteredWork CreateRegisteredWork(
        string name,
        Action<IWorkAuthorizationBuilder> authorize)
    {
        var builder = new WorkAuthorizationBuilder();
        authorize(builder);
        return CreateRegisteredWork(name, builder.BuildRegistration());
    }

    private static RegisteredWork CreateRegisteredWork(
        string name,
        WorkAuthorizationRegistration registration)
        => new(
            WorkDefinition.Create(
                name,
                authorization: registration.DefinitionAuthorization),
            _ => new NoopExecutor(),
            [],
            [],
            [],
            registration.OperateAuthorization);

    private static WorkerSnapshot CreateWorkerSnapshot(
        WorkerId workerId,
        WorkDefinition definition,
        long revision = 1,
        WorkInput? input = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerSnapshot(
            workerId,
            revision,
            1,
            definition.Name,
            definition.Category,
            null,
            null,
            new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            input,
            null,
            WorkerOptions.Default,
            definition.Configuration,
            [],
            null,
            now,
            now,
            now);
    }

    private static WorkerOverviewItem CreateWorkerOverview(
        WorkerId workerId,
        WorkDefinition definition,
        long revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerOverviewItem(
            workerId,
            definition.Name,
            null,
            null,
            new HashSet<WorkIdentifier>(),
            revision,
            definition.Category,
            WorkerState.Queued,
            null,
            now,
            now,
            now);
    }

    private static WorkRequestContext CreateRequestContext(bool isKnownAuthenticatedUser)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor: isKnownAuthenticatedUser ? new WorkActor(Id: "known-user", Name: "Known User") : null,
            isAuthenticated: isKnownAuthenticatedUser);

    private static IReadOnlySet<string> Groups(IEnumerable<string> groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record QueueInput(string Value);

    private sealed record RecordedAction(WorkerVersion Worker, WorkAction Action);

    private sealed record RecordedReconfigure(WorkerVersion Worker, WorkerReconfiguration Changes);

    private sealed class RecordingWorkerOperations : IWorkerOperations
    {
        public List<RecordedAction> Executed { get; } = [];

        public List<WorkerActionRequest> ExecutedRequests { get; } = [];

        public List<RecordedReconfigure> Reconfigured { get; } = [];

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkAction action,
            CancellationToken cancellationToken = default)
        {
            this.Executed.Add(new RecordedAction(worker, action));
            return Task.FromResult(WorkActionOutcome.NotFound(action, worker.WorkerId));
        }

        public Task<WorkActionOutcome> Execute(
            WorkerVersion worker,
            WorkerActionRequest request,
            CancellationToken cancellationToken = default)
        {
            this.ExecutedRequests.Add(request);
            return this.Execute(worker, request.Action, cancellationToken);
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
        {
            this.Reconfigured.Add(new RecordedReconfigure(worker, changes));
            return Task.FromResult(WorkActionOutcome.NotFound(WorkAction.Start, worker.WorkerId));
        }
    }

    private sealed class RecordingWorkQueryService : IWorkQueryService
    {
        public Dictionary<WorkerId, WorkerSnapshot> WorkersById { get; } = [];

        public Queue<IReadOnlyList<WorkerOverviewItem>> WorkerPages { get; } = [];

        public int WorkerCallCount { get; private set; }

        public int WorkersCallCount { get; private set; }

        public WorkerCriteria? LastWorkersCriteria { get; private set; }

        public Task<WorkerSnapshot?> Worker(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
        {
            this.WorkerCallCount++;
            return Task.FromResult(this.WorkersById.TryGetValue(workerId, out var worker) ? worker : null);
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

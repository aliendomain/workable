using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class SessionWorkQueryServiceShould
{
    [Fact]
    public async Task ExposeRequestContextAndDelegateEveryQuery()
    {
        var inner = new RecordingWorkQueryService();
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.DotNet,
            new WorkActor("session-user", "Session User"),
            "Session query test.");
        var query = new SessionWorkQueryService(inner, requestContext);
        var workerId = WorkerId.New();
        var iteration = new WorkerIterationReference(workerId, 42);
        var definitionId = WorkDefinitionId.New();
        var workers = new WorkerCriteria(DefinitionName: "session.workers", Take: 7);
        var iterations = new WorkerIterationCriteria(DefinitionName: "session.iterations", Take: 8);
        var definitions = new WorkDefinitionCriteria(Name: "session.definition");
        var workerKeys = new WorkerKeyCriteria(Type: "tenant", Take: 9);
        var workerKeyTypes = new WorkerKeyTypeCriteria(Type: "tenant", Take: 10);
        var iterationKeys = new WorkIterationKeyCriteria(Type: "iteration-tenant", Take: 11);
        var iterationKeyTypes = new WorkIterationKeyTypeCriteria(Type: "iteration-tenant", Take: 12);
        var system = new WorkSystemCriteria(DefinitionName: "session.system", IncludeThroughput: true);
        var throughput = new WorkThroughputCriteria(WindowSeconds: 30, BucketSeconds: 5);
        using var cancellation = new CancellationTokenSource();

        await query.Worker(workerId, cancellation.Token);
        await query.WorkerIteration(iteration, cancellation.Token);
        await query.Workers(workers, cancellation.Token);
        await query.WorkerIterations(iterations, cancellation.Token);
        await query.WorkInfo(definitionId, cancellation.Token);
        await query.WorkInfo("session.work", cancellation.Token);
        await query.WorkDefinitions(definitions, cancellation.Token);
        await query.WorkerKeys(workerKeys, cancellation.Token);
        await query.WorkerKeyTypes(workerKeyTypes, cancellation.Token);
        await query.WorkIterationKeys(iterationKeys, cancellation.Token);
        await query.WorkIterationKeyTypes(iterationKeyTypes, cancellation.Token);
        await query.WorkerStatusSummary(workers, cancellation.Token);
        await query.SystemDetails(system, cancellation.Token);
        await query.SystemThroughput(system, throughput, cancellation.Token);
        await query.SystemThroughputSummary(system, throughput, cancellation.Token);
        await query.SystemWorkerCounts(system, cancellation.Token);
        await query.SystemIterationCounts(system, cancellation.Token);
        await query.SystemCommonKeyTypes(system, cancellation.Token);
        await query.SystemFailedWorkers(system, cancellation.Token);
        await query.SystemFailedIterations(system, cancellation.Token);
        await query.SystemCompletedIterations(system, cancellation.Token);

        Assert.Same(requestContext, query.RequestContext);
        Assert.Collection(
            inner.Calls,
            call => AssertCall(call, "Worker", cancellation.Token, workerId),
            call => AssertCall(call, "WorkerIteration", cancellation.Token, iteration),
            call => AssertCall(call, "Workers", cancellation.Token, workers),
            call => AssertCall(call, "WorkerIterations", cancellation.Token, iterations),
            call => AssertCall(call, "WorkInfoById", cancellation.Token, definitionId),
            call => AssertCall(call, "WorkInfoByName", cancellation.Token, "session.work"),
            call => AssertCall(call, "WorkDefinitions", cancellation.Token, definitions),
            call => AssertCall(call, "WorkerKeys", cancellation.Token, workerKeys),
            call => AssertCall(call, "WorkerKeyTypes", cancellation.Token, workerKeyTypes),
            call => AssertCall(call, "WorkIterationKeys", cancellation.Token, iterationKeys),
            call => AssertCall(call, "WorkIterationKeyTypes", cancellation.Token, iterationKeyTypes),
            call => AssertCall(call, "WorkerStatusSummary", cancellation.Token, workers),
            call => AssertCall(call, "SystemDetails", cancellation.Token, system),
            call => AssertCall(call, "SystemThroughput", cancellation.Token, system, throughput),
            call => AssertCall(call, "SystemThroughputSummary", cancellation.Token, system, throughput),
            call => AssertCall(call, "SystemWorkerCounts", cancellation.Token, system),
            call => AssertCall(call, "SystemIterationCounts", cancellation.Token, system),
            call => AssertCall(call, "SystemCommonKeyTypes", cancellation.Token, system),
            call => AssertCall(call, "SystemFailedWorkers", cancellation.Token, system),
            call => AssertCall(call, "SystemFailedIterations", cancellation.Token, system),
            call => AssertCall(call, "SystemCompletedIterations", cancellation.Token, system));
    }

    private static void AssertCall(
        RecordedQueryCall call,
        string method,
        CancellationToken cancellationToken,
        params object?[] expectedArguments)
    {
        Assert.Equal(method, call.Method);
        Assert.Equal(cancellationToken, call.CancellationToken);
        Assert.Equal(expectedArguments.Length, call.Arguments.Count);

        for (var i = 0; i < expectedArguments.Length; i++)
        {
            AssertArgument(expectedArguments[i], call.Arguments[i]);
        }
    }

    private static void AssertArgument(object? expected, object? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        if (expected is WorkerCriteria or
            WorkerIterationCriteria or
            WorkDefinitionCriteria or
            WorkerKeyCriteria or
            WorkerKeyTypeCriteria or
            WorkIterationKeyCriteria or
            WorkIterationKeyTypeCriteria or
            WorkSystemCriteria or
            WorkThroughputCriteria)
        {
            Assert.Same(expected, actual);
            return;
        }

        Assert.Equal(expected, actual);
    }

    private sealed record RecordedQueryCall(
        string Method,
        IReadOnlyList<object?> Arguments,
        CancellationToken CancellationToken);

    private sealed class RecordingWorkQueryService : IWorkQueryService
    {
        public List<RecordedQueryCall> Calls { get; } = [];

        public Task<WorkerSnapshot?> Worker(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
            => this.Record<WorkerSnapshot?>("Worker", cancellationToken, null, workerId);

        public Task<WorkerIterationSnapshot?> WorkerIteration(
            WorkerIterationReference iteration,
            CancellationToken cancellationToken = default)
            => this.Record<WorkerIterationSnapshot?>("WorkerIteration", cancellationToken, null, iteration);

        public Task<WorkerQueryResult> Workers(
            WorkerCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "Workers",
                cancellationToken,
                new WorkerQueryResult([], 0, 0, 0),
                criteria);

        public Task<WorkerIterationQueryResult> WorkerIterations(
            WorkerIterationCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkerIterations",
                cancellationToken,
                new WorkerIterationQueryResult([], 0, 0, 0),
                criteria);

        public Task<WorkInfo?> WorkInfo(
            WorkDefinitionId definitionId,
            CancellationToken cancellationToken = default)
            => this.Record<WorkInfo?>("WorkInfoById", cancellationToken, null, definitionId);

        public Task<WorkInfo?> WorkInfo(
            string name,
            CancellationToken cancellationToken = default)
            => this.Record<WorkInfo?>("WorkInfoByName", cancellationToken, null, name);

        public Task<WorkDefinitionQueryResult> WorkDefinitions(
            WorkDefinitionCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkDefinitions",
                cancellationToken,
                new WorkDefinitionQueryResult([]),
                criteria);

        public Task<WorkerKeyQueryResult> WorkerKeys(
            WorkerKeyCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkerKeys",
                cancellationToken,
                new WorkerKeyQueryResult([], 0, 0, 0),
                criteria);

        public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
            WorkerKeyTypeCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkerKeyTypes",
                cancellationToken,
                new WorkerKeyTypeQueryResult([], 0, 0, 0),
                criteria);

        public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
            WorkIterationKeyCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkIterationKeys",
                cancellationToken,
                new WorkIterationKeyQueryResult([], 0, 0, 0),
                criteria);

        public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
            WorkIterationKeyTypeCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkIterationKeyTypes",
                cancellationToken,
                new WorkIterationKeyTypeQueryResult([], 0, 0, 0),
                criteria);

        public Task<WorkerStatusSummary> WorkerStatusSummary(
            WorkerCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "WorkerStatusSummary",
                cancellationToken,
                new WorkerStatusSummary(0, 0, 0, new Dictionary<WorkerState, int>()),
                criteria);

        public Task<WorkSystemDetails> SystemDetails(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemDetails",
                cancellationToken,
                new WorkSystemDetails(
                    null,
                    WorkSystemState.Started,
                    0,
                    0,
                    0,
                    0,
                    new Dictionary<WorkerState, int>(),
                    null,
                    0,
                    0,
                    0,
                    0,
                    new Dictionary<WorkCompletionStatus, int>(),
                    [],
                    null,
                    [],
                    [],
                    []),
                criteria);

        public Task<WorkSystemThroughput> SystemThroughput(
            WorkSystemCriteria? criteria = null,
            WorkThroughputCriteria? throughput = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemThroughput",
                cancellationToken,
                new WorkSystemThroughput(
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    0,
                    0,
                    0,
                    [],
                    EmptyExecutionSummary(),
                    EmptyLiveSummary()),
                criteria,
                throughput);

        public Task<WorkSystemThroughputSummary> SystemThroughputSummary(
            WorkSystemCriteria? criteria = null,
            WorkThroughputCriteria? throughput = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemThroughputSummary",
                cancellationToken,
                new WorkSystemThroughputSummary(
                    0,
                    0,
                    EmptyExecutionSummary(),
                    EmptyLiveSummary()),
                criteria,
                throughput);

        public Task<WorkSystemWorkerCounts> SystemWorkerCounts(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemWorkerCounts",
                cancellationToken,
                new WorkSystemWorkerCounts(
                    0,
                    0,
                    0,
                    0,
                    new Dictionary<WorkerState, int>(),
                    null),
                criteria);

        public Task<WorkSystemIterationCounts> SystemIterationCounts(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemIterationCounts",
                cancellationToken,
                new WorkSystemIterationCounts(
                    0,
                    0,
                    0,
                    0,
                    new Dictionary<WorkCompletionStatus, int>()),
                criteria);

        public Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemCommonKeyTypes",
                cancellationToken,
                new WorkIterationKeyTypeFacetQueryResult([]),
                criteria);

        public Task<WorkSystemFailedWorkers> SystemFailedWorkers(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemFailedWorkers",
                cancellationToken,
                new WorkSystemFailedWorkers(
                    0,
                    0,
                    0,
                    new Dictionary<WorkerState, int>(),
                    []),
                criteria);

        public Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemFailedIterations",
                cancellationToken,
                new WorkerIterationOverviewQueryResult([]),
                criteria);

        public Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
            WorkSystemCriteria? criteria = null,
            CancellationToken cancellationToken = default)
            => this.Record(
                "SystemCompletedIterations",
                cancellationToken,
                new WorkerIterationOverviewQueryResult([]),
                criteria);

        private Task<T> Record<T>(
            string method,
            CancellationToken cancellationToken,
            T result,
            params object?[] arguments)
        {
            this.Calls.Add(new RecordedQueryCall(method, arguments, cancellationToken));
            return Task.FromResult(result);
        }

        private static WorkThroughputExecutionSummary EmptyExecutionSummary()
            => new(0, 0, 0, 0, 0);

        private static WorkThroughputLiveSummary EmptyLiveSummary()
            => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}

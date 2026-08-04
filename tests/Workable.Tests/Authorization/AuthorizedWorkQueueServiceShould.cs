using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkQueueServiceShould
{
    [Fact]
    public async Task ReturnNotFoundWithoutCallingInnerForUnknownDefinitions()
    {
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works:
            [
                CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate")),
            ],
            out _,
            out var inner);

        var byName = await queue.Enqueue("missing.work");

        Assert.Equal(WorkQueueStatus.NotFound, byName.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task ReturnUnauthorizedWithoutCallingInnerForInoperableDefinitions()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var hidden = CreateRegisteredWork("hidden.work", authorize => authorize.AllowOperateToGroups("hidden.operate"));
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works: [visible, hidden],
            out _,
            out var inner);

        var byName = await queue.Enqueue(hidden.Definition.Name);

        Assert.Equal(WorkQueueStatus.Unauthorized, byName.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task DelegateEveryEnqueueOverloadForOperableDefinitions()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var inner);
        var input = WorkInput.FromValue(new QueueInput("direct"), WorkData.DefaultJsonOptions);
        var typedByName = new QueueInput("typed-name");
        var options = WorkerOptions.Default with { ProfilingEnabled = true };
        using var cancellation = new CancellationTokenSource();

        await queue.Enqueue(visible.Definition.Name, input, options, cancellation.Token);
        await queue.Enqueue(visible.Definition.Name, typedByName, options, cancellation.Token);

        Assert.Equal(
            [
                new RecordedQueueCall("name", null, visible.Definition.Name, input, options, cancellation.Token),
                new RecordedQueueCall("name", null, visible.Definition.Name, WorkInput.FromValue(typedByName, WorkData.DefaultJsonOptions), options, cancellation.Token),
            ],
            inner.Calls);
    }

    [Fact]
    public async Task ApplyCommonOperateRequirementsToQueueing()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenOperatingRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var inner);

        var accepted = await queue.Enqueue(visible.Definition.Name, new QueueInput("allowed"));
        var rejected = await queue.Enqueue(visible.Definition.Name, new QueueInput("denied"));

        Assert.True(accepted.QueueOutcome.IsAccepted);
        Assert.Equal(WorkQueueStatus.Unauthorized, rejected.QueueOutcome.Status);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task AllowKnownAuthenticatedQueueingAndShortCircuitAfterFirstTrue()
    {
        var falseCalls = 0;
        var trueCalls = 0;
        var skippedCalls = 0;
        var builder = new WorkAuthorizationBuilder();
        builder.AllowOperateToKnownAuthenticatedUsers(
            operate => operate
                .WhenQueueingRequire<QueueInput>(_ =>
                {
                    falseCalls++;
                    return false;
                })
                .WhenQueueingRequire<QueueInput>(context =>
                {
                    trueCalls++;
                    return context.Input?.Value == "allowed";
                })
                .WhenQueueingRequire<QueueInput>(_ =>
                {
                    skippedCalls++;
                    return true;
                }));
        var registration = builder.BuildRegistration();
        var visible = CreateRegisteredWork("known.work", registration);
        var queue = CreateQueueService(
            groups: [],
            works: [visible],
            out _,
            out var inner,
            isKnownAuthenticatedUser: true);

        var accepted = await queue.Enqueue(visible.Definition.Name, new QueueInput("allowed"));

        Assert.True(accepted.QueueOutcome.IsAccepted);
        Assert.Equal(1, falseCalls);
        Assert.Equal(1, trueCalls);
        Assert.Equal(0, skippedCalls);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task ReturnInvalidWhenTypedQueueRequirementCannotDeserializeInput()
    {
        var visible = CreateRegisteredWork(
            "invalid.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenQueueingRequire<QueueInput>(context => context.Input?.Value == "allowed")));
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works: [visible],
            out _,
            out var inner);

        var outcome = await queue.Enqueue(
            visible.Definition.Name,
            WorkInput.FromJson("\"not-an-object\""));

        Assert.Equal(WorkQueueStatus.Invalid, outcome.QueueOutcome.Status);
        Assert.Contains(outcome.QueueOutcome.Messages, message => message.Code == "workable.authorization.operate_requirement_input_invalid");
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task AllowQueueOnlyGrantsWithoutWorkerActionPermission()
    {
        var visible = CreateRegisteredWork(
            "queue.only.work",
            authorize => authorize.AllowQueueToGroups("visible.queue"));
        var queue = CreateQueueService(
            groups: ["visible.queue"],
            works: [visible],
            out _,
            out var inner);

        var outcome = await queue.Enqueue(visible.Definition.Name, new QueueInput("queued"));

        Assert.True(outcome.QueueOutcome.IsAccepted);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task DoNotTreatWorkerActionOnlyGrantsAsQueuePermission()
    {
        var visible = CreateRegisteredWork(
            "actions.only.work",
            authorize => authorize.AllowWorkerActionsToGroups("visible.actions"));
        var queue = CreateQueueService(
            groups: ["visible.actions"],
            works: [visible],
            out _,
            out var inner);

        var outcome = await queue.Enqueue(visible.Definition.Name, new QueueInput("denied"));

        Assert.Equal(WorkQueueStatus.Unauthorized, outcome.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task BypassConstrainedQueueRequirementsForSystemOperateAllAccess()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize.AllowOperateToGroups(
                ["visible.operate"],
                operate => operate.WhenQueueingRequire<QueueInput>(_ => false)));
        var queue = CreateQueueService(
            groups: ["ops.operateall"],
            works: [visible],
            out _,
            out var inner,
            systemAuthorizationConfiguration: WorkSystemAuthorizationConfiguration.Default with
            {
                OperateAllWorkGroups = Groups(["ops.operateall"]),
            });

        var outcome = await queue.Enqueue(visible.Definition.Name, new QueueInput("denied"));

        Assert.True(outcome.QueueOutcome.IsAccepted);
        Assert.Single(inner.Calls);
    }

    private static AuthorizedWorkQueueService CreateQueueService(
        IReadOnlyList<string> groups,
        IReadOnlyList<RegisteredWork> works,
        out WorkSystemCatalog catalog,
        out RecordingWorkQueueService inner,
        bool isKnownAuthenticatedUser = false,
        WorkSystemAuthorizationConfiguration? systemAuthorizationConfiguration = null)
    {
        catalog = new WorkSystemCatalog(works, persistenceStoreAvailable: false);
        inner = new RecordingWorkQueueService();
        var requestContext = CreateRequestContext(isKnownAuthenticatedUser);
        var normalizedGroups = Groups(groups);
        return new AuthorizedWorkQueueService(
            catalog,
            inner,
            new WorkAuthorizationEvaluator(
                catalog,
                normalizedGroups,
                isKnownAuthenticatedUser,
                systemAuthorizationConfiguration is null
                    ? null
                    : new WorkSystemAuthorizationEvaluator(systemAuthorizationConfiguration, normalizedGroups)),
            requestContext,
            canViewDiagnostics: false);
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

    private static WorkRequestContext CreateRequestContext(bool isKnownAuthenticatedUser)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor: isKnownAuthenticatedUser ? new WorkActor(Id: "known-user", Name: "Known User") : null,
            isAuthenticated: isKnownAuthenticatedUser);

    private static IReadOnlySet<string> Groups(IEnumerable<string> groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record QueueInput(string Value);

    private sealed record RecordedQueueCall(
        string Overload,
        WorkDefinitionId? DefinitionId,
        string? Name,
        object? Input,
        WorkerOptions? Options,
        CancellationToken CancellationToken);

    private sealed class RecordingWorkQueueService : IWorkQueueService
    {
        public List<RecordedQueueCall> Calls { get; } = [];

        public Task<IWorkerHandle> Enqueue(
            string name,
            WorkInput? input = null,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(new("name", null, name, input, options, cancellationToken));
            return Accepted();
        }

        public Task<IWorkerHandle> Enqueue<TInput>(
            string name,
            TInput input,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(new("name-typed", null, name, input, options, cancellationToken));
            return Accepted();
        }

        private static Task<IWorkerHandle> Accepted()
            => Task.FromResult<IWorkerHandle>(new RecordingWorkerHandle(
                WorkQueueOutcome.Accepted(WorkerId.New())));
    }

    private sealed class RecordingWorkerHandle(WorkQueueOutcome queueOutcome) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId => this.QueueOutcome.WorkerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
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

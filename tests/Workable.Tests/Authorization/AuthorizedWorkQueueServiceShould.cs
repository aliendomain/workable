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
        var typed = await queue.Enqueue("missing.work", new QueueInput("missing"));

        Assert.Equal(WorkQueueStatus.NotFound, byName.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.NotFound, typed.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task ReturnNotFoundWithoutCallingInnerForHiddenInoperableDefinitions()
    {
        var visible = CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate"));
        var hidden = CreateRegisteredWork("hidden.work", authorize => authorize.AllowOperateToGroups("hidden.operate"));
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works: [visible, hidden],
            out _,
            out var inner);

        var byName = await queue.Enqueue(hidden.Definition.Name);
        var typed = await queue.Enqueue(hidden.Definition.Name, new QueueInput("hidden"));

        Assert.Equal(WorkQueueStatus.NotFound, byName.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.NotFound, typed.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task ReturnUnauthorizedForDiscoverableDefinitionsOutsideQueueScope()
    {
        var visible = CreateRegisteredWork(
            "visible.work",
            authorize => authorize
                .AllowReadToGroups("visible.read")
                .AllowQueueToGroups("visible.queue"));
        var queue = CreateQueueService(
            groups: ["visible.read"],
            works: [visible],
            out _,
            out var inner);

        var byName = await queue.Enqueue(visible.Definition.Name);

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
    public void ForwardDurableWorkNotificationsToInnerQueue()
    {
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            works:
            [
                CreateRegisteredWork("visible.work", authorize => authorize.AllowOperateToGroups("visible.operate")),
            ],
            out _,
            out var inner);

        queue.NotifyDurableWorkAvailable();

        Assert.Equal(1, inner.DurableWorkNotifications);
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

    [Fact]
    public async Task RedactWorkerAndUnhandledExceptionDetailsFromQueueOnlyCompletions()
    {
        var visible = CreateRegisteredWork(
            "queue.only.completion",
            authorize => authorize.AllowQueueToGroups("visible.queue"));
        var queue = CreateQueueService(
            groups: ["visible.queue"],
            works: [visible],
            out _,
            out var inner);
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        inner.Completion = new WorkCompletion(
            WorkCompletionStatus.Failed,
            CreateWorker(visible.Definition.Name),
            Output: WorkOutput.FromValue("secret completion output"),
            Messages:
            [
                new WorkMessage(
                    "workable.execution.exception",
                    WorkMessageSeverity.Error,
                    "System.InvalidOperationException: secret stack",
                    Metadata: new Dictionary<string, object?> { ["exception"] = "secret" })
                {
                    OccurredAt = occurredAt,
                },
                WorkMessage.Error("workable.test.safe", "safe detail"),
            ]);

        var handle = await queue.Enqueue(visible.Definition.Name);
        var raw = await handle.WaitForCompletion();
        var typed = await handle.WaitForCompletion<string>();

        Assert.Null(raw.Worker);
        Assert.Null(raw.Output);
        Assert.Null(typed.Worker);
        Assert.Null(typed.Output);
        Assert.Null(typed.RawOutput);
        Assert.Equal(WorkCompletionStatus.Failed, raw.Status);
        Assert.Equal("Work execution failed with an unhandled exception.", raw.Messages[0].Text);
        Assert.Null(raw.Messages[0].Metadata);
        Assert.Equal(occurredAt, raw.Messages[0].OccurredAt);
        Assert.Equal("safe detail", raw.Messages[1].Text);
        Assert.Equal(raw.Messages, typed.Messages);
    }

    [Fact]
    public async Task RedactQueueOnlyOutputBeforeTypedDeserialization()
    {
        var visible = CreateRegisteredWork(
            "queue.only.malformed.completion",
            authorize => authorize.AllowQueueToGroups("visible.queue"));
        var queue = CreateQueueService(
            groups: ["visible.queue"],
            works: [visible],
            out _,
            out var inner);
        inner.Completion = new WorkCompletion(
            WorkCompletionStatus.Completed,
            CreateWorker(visible.Definition.Name),
            Output: WorkOutput.FromJson("{malformed-json"),
            Messages: []);

        var completion = await (await queue.Enqueue(visible.Definition.Name))
            .WaitForCompletion<QueueInput>();

        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Null(completion.Worker);
        Assert.Null(completion.Output);
        Assert.Null(completion.RawOutput);
    }

    [Fact]
    public async Task PreserveWorkerAndExceptionDetailsWhenReadIsGranted()
    {
        var visible = CreateRegisteredWork(
            "readable.completion",
            authorize =>
            {
                authorize.AllowQueueToGroups("visible.queue");
                authorize.AllowReadToGroups("visible.read");
            });
        var queue = CreateQueueService(
            groups: ["visible.queue", "visible.read"],
            works: [visible],
            out _,
            out var inner);
        var worker = CreateWorker(visible.Definition.Name);
        var output = WorkOutput.FromValue("retained completion output");
        var exception = WorkMessage.Error(
            "workable.execution.exception",
            "System.InvalidOperationException: retained detail");
        inner.Completion = new WorkCompletion(WorkCompletionStatus.Failed, worker, output, [exception]);

        var completion = await (await queue.Enqueue(visible.Definition.Name)).WaitForCompletion();

        Assert.Same(worker, completion.Worker);
        Assert.Same(output, completion.Output);
        Assert.Same(exception, Assert.Single(completion.Messages));
    }

    [Fact]
    public async Task RedactPersistenceProviderDetailsFromQueueOnlyOutcomes()
    {
        var visible = CreateRegisteredWork(
            "queue.only.persistence.failure",
            authorize => authorize.AllowQueueToGroups("visible.queue"));
        var queue = CreateQueueService(
            groups: ["visible.queue"],
            works: [visible],
            out _,
            out var inner);
        var providerFailure = new WorkMessage(
            "workable.queue_durability.store_unreachable",
            WorkMessageSeverity.Error,
            "Server=db.internal;Password=secret",
            Metadata: new Dictionary<string, object?> { ["provider"] = "secret" });
        inner.NextQueueOutcome = WorkQueueOutcome.Invalid(
            [providerFailure, WorkMessage.Error("workable.test.safe", "safe detail")]);

        var handle = await queue.Enqueue(visible.Definition.Name);
        var outcome = handle.QueueOutcome;

        Assert.Equal(WorkQueueStatus.Invalid, outcome.Status);
        Assert.Equal(
            "The persistence store required for durable queueing is currently unavailable.",
            outcome.Messages[0].Text);
        Assert.Null(outcome.Messages[0].Metadata);
        Assert.Equal("safe detail", outcome.Messages[1].Text);
        Assert.Same(providerFailure, inner.NextQueueOutcome.Messages[0]);
        Assert.Same(outcome, handle.QueueOutcome);
    }

    [Fact]
    public async Task PreservePersistenceProviderDetailsInQueueOutcomesWhenReadIsGranted()
    {
        var visible = CreateRegisteredWork(
            "readable.persistence.failure",
            authorize =>
            {
                authorize.AllowQueueToGroups("visible.queue");
                authorize.AllowReadToGroups("visible.read");
            });
        var queue = CreateQueueService(
            groups: ["visible.queue", "visible.read"],
            works: [visible],
            out _,
            out var inner);
        inner.NextQueueOutcome = WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.idempotency.persistence_store_unreachable",
                "diagnostic provider detail")]);

        var handle = await queue.Enqueue(visible.Definition.Name);

        Assert.Same(inner.NextQueueOutcome, handle.QueueOutcome);
        Assert.Equal("diagnostic provider detail", Assert.Single(handle.QueueOutcome.Messages).Text);
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

    private static WorkerSnapshot CreateWorker(string definitionName)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerSnapshot(
            WorkerId.New(),
            Revision: 1,
            StateSequence: 1,
            DefinitionName: definitionName,
            DefinitionCategory: "Tests",
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            RequestContext: WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            State: WorkerState.Failed,
            Input: null,
            Output: null,
            Options: WorkerOptions.Default,
            Configuration: WorkConfiguration.Default,
            Messages: [],
            InterruptionReason: null,
            CreatedAt: now,
            StateChangedAt: now,
            UpdatedAt: now);
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

        public WorkQueueOutcome? NextQueueOutcome { get; set; }

        public WorkCompletion Completion { get; set; } = new(
            WorkCompletionStatus.Completed,
            Worker: null,
            Output: null,
            Messages: []);

        public int DurableWorkNotifications { get; private set; }

        public void NotifyDurableWorkAvailable()
            => this.DurableWorkNotifications++;

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

        private Task<IWorkerHandle> Accepted()
            => Task.FromResult<IWorkerHandle>(new RecordingWorkerHandle(
                this.NextQueueOutcome ?? WorkQueueOutcome.Accepted(WorkerId.New()),
                this.Completion));
    }

    private sealed class RecordingWorkerHandle(
        WorkQueueOutcome queueOutcome,
        WorkCompletion completion) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId => this.QueueOutcome.WorkerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => Task.FromResult(completion);

        public Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => Task.FromResult(completion.ToTyped<TOutput>());
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

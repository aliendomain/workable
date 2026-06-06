using System.Collections.Concurrent;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Queueing")]
public sealed class WorkQueueAcceptanceCoordinatorShould
{
    [Fact]
    public void PrepareDurableQueueRequestsWithoutMaterializingAWorker()
    {
        var coordinator = CreateCoordinator();
        var definition = CreateDefinition("durable.acceptance", DurableIdempotentCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var workerId = WorkerId.New();
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "durable"));
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.DotNet, description: "Durable acceptance test.");

        var prepared = coordinator.Prepare(
            workerId,
            registeredWork,
            input,
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            requestContext,
            DateTimeOffset.UtcNow);

        Assert.True(prepared.Outcome.IsAccepted);
        Assert.Equal(workerId, prepared.Outcome.WorkerId);
        Assert.Null(prepared.Worker);
        Assert.NotNull(prepared.PersistenceRequest);
        Assert.Null(prepared.IdempotencyRequest);
        Assert.False(prepared.ShouldScheduleStart);
        Assert.False(prepared.ShouldDrainQueuedWorkers);
        Assert.Equal(workerId, prepared.PersistenceRequest.WorkerId);
        Assert.Equal(definition.Id, prepared.PersistenceRequest.Definition.Id);
        Assert.Equal(input.SubjectId, prepared.PersistenceRequest.Idempotency?.SubjectId);
        Assert.Equal(requestContext, prepared.PersistenceRequest.RequestContext);
    }

    [Fact]
    public void PreparePersistenceBackedIdempotencyWithoutDurableQueueAsInMemoryWork()
    {
        var coordinator = CreateCoordinator();
        var definition = CreateDefinition("persistent.idempotency", PersistentIdempotencyCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var workerId = WorkerId.New();
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotent"));
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.DotNet, description: "Persistent idempotency acceptance test.");

        var prepared = coordinator.Prepare(
            workerId,
            registeredWork,
            input,
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            requestContext,
            DateTimeOffset.UtcNow);

        Assert.True(prepared.Outcome.IsAccepted);
        Assert.Null(prepared.PersistenceRequest);
        Assert.NotNull(prepared.Worker);
        Assert.NotNull(prepared.IdempotencyRequest);
        Assert.True(prepared.ShouldScheduleStart);
        Assert.False(prepared.ShouldDrainQueuedWorkers);
        Assert.Equal(workerId, prepared.Worker.Id);
        Assert.Equal(input.SubjectId, prepared.IdempotencyRequest.SubjectId);
        Assert.Equal(requestContext, prepared.IdempotencyRequest.RequestContext);
    }

    [Fact]
    public void RejectCallerOwnedTransactionsWhenPersistenceBackedIdempotencyIsNotDurablyQueued()
    {
        var coordinator = CreateCoordinator();
        var definition = CreateDefinition("persistent.idempotency.transaction", PersistentIdempotencyCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var workerId = WorkerId.New();
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "transaction"));

        var prepared = coordinator.Prepare(
            workerId,
            registeredWork,
            input,
            RegisteredWorkRuntimePlan.Create(
                definition,
                WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() }),
            WorkRequestContext.Create(WorkInvocationChannel.DotNet),
            DateTimeOffset.UtcNow);

        Assert.Equal(WorkQueueStatus.Invalid, prepared.Outcome.Status);
        Assert.Null(prepared.Worker);
        Assert.Null(prepared.PersistenceRequest);
        Assert.Null(prepared.IdempotencyRequest);
        Assert.Contains(prepared.Outcome.Messages, message =>
            message.Code == "workable.idempotency.persistence_transaction_requires_durable_queue" &&
            message.Target == "options.queueDurabilityTransaction");
    }

    [Fact]
    public void RejectIdempotentWorkWithoutASubjectBeforePreparingPersistence()
    {
        var coordinator = CreateCoordinator();
        var definition = CreateDefinition("idempotency.requires.subject", DurableIdempotentCoordination());
        var registeredWork = CreateRegisteredWork(definition);

        var prepared = coordinator.Prepare(
            WorkerId.New(),
            registeredWork,
            input: null,
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            WorkRequestContext.Create(WorkInvocationChannel.DotNet),
            DateTimeOffset.UtcNow);

        Assert.Equal(WorkQueueStatus.Invalid, prepared.Outcome.Status);
        Assert.Null(prepared.Worker);
        Assert.Null(prepared.PersistenceRequest);
        Assert.Contains(prepared.Outcome.Messages, message =>
            message.Code == "workable.idempotency.subject_required" &&
            message.Target == "input.subjectId");
    }

    [Fact]
    public void DeferInMemoryWorkersWhenConcurrencyCapacityIsFull()
    {
        var coordinator = CreateCoordinator();
        var definition = CreateDefinition(
            "concurrency.deferred.acceptance",
            ConcurrencyCoordination(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var registeredWork = CreateRegisteredWork(definition);
        var runtimePlan = RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default);
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.DotNet);

        var first = coordinator.Prepare(
            WorkerId.New(),
            registeredWork,
            input: null,
            runtimePlan,
            requestContext,
            DateTimeOffset.UtcNow);
        var second = coordinator.Prepare(
            WorkerId.New(),
            registeredWork,
            input: null,
            runtimePlan,
            requestContext,
            DateTimeOffset.UtcNow);

        Assert.True(first.Outcome.IsAccepted);
        Assert.NotNull(first.Worker);
        Assert.False(first.Worker.IsStartDeferred);
        Assert.True(first.ShouldScheduleStart);
        Assert.True(second.Outcome.IsAccepted);
        Assert.NotNull(second.Worker);
        Assert.True(second.Worker.IsStartDeferred);
        Assert.False(second.ShouldScheduleStart);
        Assert.Contains(second.Outcome.Messages, message => message.Code == "workable.concurrency.start_deferred");
    }

    [Fact]
    public void RejectInMemoryWorkersWhenConcurrencyCapacityIsFullAndDeferralIsDisabled()
    {
        var coordinator = CreateCoordinator();
        var definition = CreateDefinition(
            "concurrency.rejected.acceptance",
            ConcurrencyCoordination(WorkConcurrencyLimitReachedBehavior.Ignore));
        var registeredWork = CreateRegisteredWork(definition);
        var runtimePlan = RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default);
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.DotNet);

        var first = coordinator.Prepare(
            WorkerId.New(),
            registeredWork,
            input: null,
            runtimePlan,
            requestContext,
            DateTimeOffset.UtcNow);
        var second = coordinator.Prepare(
            WorkerId.New(),
            registeredWork,
            input: null,
            runtimePlan,
            requestContext,
            DateTimeOffset.UtcNow);

        Assert.True(first.Outcome.IsAccepted);
        Assert.NotNull(first.Worker);
        Assert.Equal(WorkQueueStatus.Invalid, second.Outcome.Status);
        Assert.Null(second.Worker);
        Assert.Contains(second.Outcome.Messages, message => message.Code == "workable.concurrency.capacity_reached");
    }

    private static WorkQueueAcceptanceCoordinator CreateCoordinator()
    {
        var durability = new WorkQueueDurabilityCoordinator(
            store: null,
            WorkSystemId.New(),
            workSystemName: "acceptance-tests",
            new WorkSystemIdempotencyDiagnosticsTracker(),
            isAcceptingWork: () => true,
            getSystemExecutionToken: () => CancellationToken.None,
            acceptPersistedEntry: (_, _) => Task.CompletedTask,
            leaseLost: _ => { });
        return new WorkQueueAcceptanceCoordinator(
            new WorkIdempotencyCoordinator(
                new WorkerIndex(),
                new ConcurrentDictionary<WorkerId, WorkerRecord>(),
                new WorkSystemIdempotencyDiagnosticsTracker()),
            new WorkConcurrencyCoordinator(),
            durability);
    }

    private static WorkDefinition CreateDefinition(
        string name,
        WorkCoordinationConfiguration coordination)
        => WorkDefinition.Create(
            name,
            configuration: WorkConfiguration.Default with
            {
                Coordination = coordination,
            });

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static WorkCoordinationConfiguration DurableIdempotentCoordination()
        => WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Storage = WorkCoordinationStorage.Persistent,
            Idempotency = WorkIdempotencyConfiguration.Default with
            {
                IsEnabled = true,
            },
            Durability = WorkQueueDurabilityConfiguration.Default with
            {
                IsEnabled = true,
            },
        };

    private static WorkCoordinationConfiguration PersistentIdempotencyCoordination()
        => WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Storage = WorkCoordinationStorage.Persistent,
            Idempotency = WorkIdempotencyConfiguration.Default with
            {
                IsEnabled = true,
            },
        };

    private static WorkCoordinationConfiguration ConcurrencyCoordination(
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior)
        => WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Concurrency = WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerDefinition,
                BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                LimitReachedBehavior = limitReachedBehavior,
            },
        };

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class TestQueueDurabilityTransaction : IWorkQueueDurabilityTransaction;
}

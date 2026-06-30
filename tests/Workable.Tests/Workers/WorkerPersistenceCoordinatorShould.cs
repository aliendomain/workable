using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Queueing")]
public sealed class WorkerPersistenceCoordinatorShould
{
    [Fact]
    public async Task AcceptInMemoryWorkAfterPersistenceBackedIdempotencyReservationSucceeds()
    {
        var definition = CreateDefinition("persistence.idempotency.accepted", PersistentIdempotencyCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var store = new RecordingPersistenceStore();
        var coordinator = CreateCoordinator(
            registeredWork,
            store,
            out var acceptedWorkers);
        var workerId = WorkerId.New();
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "accepted"));
        var origin = WorkOrigin.Create(WorkInvocationChannel.InProcess);
        var requestContext = new WorkRequestContext(origin);

        var acceptance = await coordinator.AcceptQueuedWorker(
            workerId,
            registeredWork,
            input,
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            requestContext,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.True(acceptance.Outcome.IsAccepted);
        Assert.NotNull(acceptance.Worker);
        Assert.Null(acceptance.Handle);
        Assert.True(acceptance.ShouldScheduleStart);
        Assert.Equal(workerId, acceptance.Worker.Id);
        Assert.Equal(acceptance.Worker, Assert.Single(acceptedWorkers));
        var reservation = Assert.Single(store.IdempotencyReservations);
        Assert.Equal(workerId, reservation.WorkerId);
        Assert.Equal(input.SubjectId, reservation.SubjectId);
        Assert.Equal(origin, reservation.Origin);
    }

    [Fact]
    public async Task RejectAndDoNotMaterializeInMemoryWorkWhenIdempotencyReservationFails()
    {
        var definition = CreateDefinition("persistence.idempotency.duplicate", PersistentIdempotencyCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var store = new RecordingPersistenceStore
        {
            RejectIdempotencyReservationsAsDuplicate = true,
        };
        var coordinator = CreateCoordinator(
            registeredWork,
            store,
            out var acceptedWorkers);

        var acceptance = await coordinator.AcceptQueuedWorker(
            WorkerId.New(),
            registeredWork,
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "duplicate")),
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(WorkQueueStatus.Invalid, acceptance.Outcome.Status);
        Assert.Null(acceptance.Worker);
        Assert.Null(acceptance.Handle);
        Assert.Empty(acceptedWorkers);
        Assert.Single(store.IdempotencyReservations);
        Assert.Contains(acceptance.Outcome.Messages, message =>
            message.Code == "workable.idempotency.duplicate_subject" &&
            message.Target == "input.subjectId");
    }

    [Fact]
    public async Task AcceptDurableQueuedWorkThroughAHandleWithoutMaterializingImmediately()
    {
        var definition = CreateDefinition("persistence.durable.accepted", DurableIdempotentCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var store = new RecordingPersistenceStore();
        var coordinator = CreateCoordinator(
            registeredWork,
            store,
            out var acceptedWorkers);
        var workerId = WorkerId.New();
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "durable"));

        var acceptance = await coordinator.AcceptQueuedWorker(
            workerId,
            registeredWork,
            input,
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.True(acceptance.Outcome.IsAccepted);
        Assert.Null(acceptance.Worker);
        Assert.NotNull(acceptance.Handle);
        Assert.Equal(workerId, acceptance.Handle.WorkerId);
        Assert.False(acceptance.ShouldScheduleStart);
        Assert.Empty(acceptedWorkers);
        var request = Assert.Single(store.EnqueueRequests);
        Assert.Equal(workerId, request.WorkerId);
        Assert.Equal(definition.Id, request.Definition.Id);
        Assert.Equal(input.SubjectId, request.Idempotency?.SubjectId);
    }

    [Fact]
    public async Task SignalDurableReaderOnlyForDurableEnqueueWithoutCallerOwnedTransaction()
    {
        var definition = CreateDefinition("persistence.durable.signals", DurableIdempotentCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var ownTransactionCoordinator = CreateCoordinator(
            registeredWork,
            new RecordingPersistenceStore(),
            out _);
        var callerTransactionCoordinator = CreateCoordinator(
            registeredWork,
            new RecordingPersistenceStore(),
            out _);

        await ownTransactionCoordinator.AcceptQueuedWorker(
            WorkerId.New(),
            registeredWork,
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "own-transaction")),
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await callerTransactionCoordinator.AcceptQueuedWorker(
            WorkerId.New(),
            registeredWork,
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "caller-transaction")),
            RegisteredWorkRuntimePlan.Create(
                definition,
                WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() }),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(1, ownTransactionCoordinator.DurableReaderSignals);
        Assert.Equal(0, callerTransactionCoordinator.DurableReaderSignals);
    }

    [Fact]
    public async Task RejectDurableQueuedWorkWhenThePersistenceStoreRejectsTheEnqueue()
    {
        var definition = CreateDefinition("persistence.durable.duplicate", DurableIdempotentCoordination());
        var registeredWork = CreateRegisteredWork(definition);
        var store = new RecordingPersistenceStore
        {
            RejectEnqueuesAsDuplicate = true,
        };
        var coordinator = CreateCoordinator(
            registeredWork,
            store,
            out var acceptedWorkers);

        var acceptance = await coordinator.AcceptQueuedWorker(
            WorkerId.New(),
            registeredWork,
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "durable-duplicate")),
            RegisteredWorkRuntimePlan.Create(definition, WorkerOptions.Default),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(WorkQueueStatus.Invalid, acceptance.Outcome.Status);
        Assert.Null(acceptance.Worker);
        Assert.Null(acceptance.Handle);
        Assert.Empty(acceptedWorkers);
        Assert.Single(store.EnqueueRequests);
        Assert.Contains(acceptance.Outcome.Messages, message =>
            message.Code == "workable.queue_durability.duplicate" &&
            message.Target == "input.subjectId");
    }

    private static WorkerPersistenceCoordinator CreateCoordinator(
        RegisteredWork registeredWork,
        IWorkPersistenceStore store,
        out List<WorkerRecord> acceptedWorkers)
    {
        var catalog = new WorkSystemCatalog([registeredWork], persistenceStoreAvailable: true);
        var workers = new ConcurrentDictionary<WorkerId, WorkerRecord>();
        var index = new WorkerIndex();
        var accepted = new List<WorkerRecord>();
        acceptedWorkers = accepted;

        return new WorkerPersistenceCoordinator(
            catalog,
            workers,
            index,
            new WorkConcurrencyCoordinator(),
            WorkSystemId.New(),
            "persistence-tests",
            store,
            new WorkSystemIdempotencyDiagnosticsTracker(),
            isAcceptingWork: () => true,
            getSystemExecutionToken: () => CancellationToken.None,
            acceptWorkerIntoMemory: worker =>
            {
                accepted.Add(worker);
                workers[worker.Id] = worker;
                index.Register(worker);
            },
            getTrackedWorker: workerId => workers.TryGetValue(workerId, out var worker) ? worker : null,
            persistedWorkerMaterialized: (_, _) => Task.CompletedTask,
            interruptWorker: (_, _) => { },
            logger: null,
            durabilityOptions: WorkQueueDurabilityRuntimeOptions.Default);
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

    private static WorkCoordinationConfiguration DurableIdempotentCoordination()
        => PersistentIdempotencyCoordination() with
        {
            Durability = WorkQueueDurabilityConfiguration.Default with
            {
                IsEnabled = true,
            },
        };

    private sealed class RecordingPersistenceStore : IWorkPersistenceStore
    {
        public List<WorkQueueDurabilityEnqueueRequest> EnqueueRequests { get; } = [];

        public List<WorkIdempotencyPersistenceRequest> IdempotencyReservations { get; } = [];

        public bool RejectEnqueuesAsDuplicate { get; set; }

        public bool RejectIdempotencyReservationsAsDuplicate { get; set; }

        public Task Initialize(
            WorkQueueDurabilityInitializationContext context,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Enqueue(
            WorkQueueDurabilityEnqueueRequest request,
            CancellationToken cancellationToken = default)
        {
            this.EnqueueRequests.Add(request);
            if (this.RejectEnqueuesAsDuplicate)
            {
                throw new WorkQueueDurabilityDuplicateException("Duplicate durable enqueue.");
            }

            return Task.CompletedTask;
        }

        public Task ReserveIdempotency(
            WorkIdempotencyPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            this.IdempotencyReservations.Add(request);
            if (this.RejectIdempotencyReservationsAsDuplicate)
            {
                throw new WorkQueueDurabilityDuplicateException("Duplicate idempotency reservation.");
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task RenewLeases(
            IReadOnlyList<WorkQueueDurabilityLease> leases,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RetainFailed(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

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

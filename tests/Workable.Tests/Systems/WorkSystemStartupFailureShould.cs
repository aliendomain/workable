using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SystemLifecycle")]
public sealed class WorkSystemStartupFailureShould
{
    [Fact]
    public async Task NotPrepareWorkersWhenWorkflowRecoveryFailsAndAllowRetry()
    {
        var store = new StartupFailurePersistenceStore
        {
            FailWorkflowListOnce = true,
        };
        await using var provider = CreateProvider(store);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(StartupFailurePersistenceStore.InjectedFailureMessage, failure.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Equal(1, store.WorkflowListCalls);
        Assert.Equal(0, store.WorkInitializeCalls);

        await system.Start();

        Assert.Equal(WorkSystemState.Started, system.State);
        Assert.Equal(2, store.WorkflowListCalls);
        Assert.Equal(1, store.WorkInitializeCalls);
        await system.Stop();
    }

    [Fact]
    public async Task CleanUpFailedWorkerPreparationAndAllowRetry()
    {
        var store = new StartupFailurePersistenceStore
        {
            FailWorkInitializeOnce = true,
        };
        await using var provider = CreateProvider(store);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(StartupFailurePersistenceStore.InjectedFailureMessage, failure.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Equal(["workflow-list", "work-initialize"], store.Operations.Take(2));

        await system.Start();
        var handle = await system.Queue.Enqueue("startup.failure.work");
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(WorkSystemState.Started, system.State);
        await system.Stop();
    }

    [Fact]
    public async Task RemoveMaterializedDurableWorkersWhenALaterStartupOperationFails()
    {
        var definition = WorkDefinition.Create("startup.failure.work");
        var workerId = WorkerId.New();
        var store = new StartupFailurePersistenceStore(new WorkQueueDurabilityEntry(
            new WorkQueueDurabilityLease(workerId, "test-owner", "test-lease"),
            definition.Name,
            Input: null,
            WorkerOptions.Default,
            WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart },
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow));
        var observer = new FailOnceStartedObserver();
        await using var provider = CreateProvider(store, observer, definition);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(FailOnceStartedObserver.InjectedFailureMessage, failure.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Null(await system.Query.Worker(workerId));
        Assert.Equal(1, observer.StoppedCalls);

        await system.Start();

        Assert.Equal(WorkSystemState.Started, system.State);
        await system.Stop();
    }

    [Fact]
    public async Task CleanUpWhenRecoveredWorkflowResumptionFailsAndAllowRetry()
    {
        var work = WorkDefinition.Create("startup.failure.workflow-child");
        var workflowDefinition = WorkflowDefinition.Create(
            "startup.failure.workflow",
            coordination: WorkflowCoordinationConfiguration.Durable);
        var store = new StartupFailurePersistenceStore
        {
            FailWorkflowDeleteOnce = true,
        };
        var services = new ServiceCollection();
        services.AddSingleton<IWorkPersistenceStore>(store);
        services.AddWorkableSystem("startup-failure-tests", builder => builder
            .RequireAuthorization(false)
            .AddWork(work, SuccessfulWork)
            .AddWorkflow(
                workflowDefinition,
                workflow => workflow.DispatchWork("dispatch", work)));
        await using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(system.Workflows.TryGet(workflowDefinition.Name, out var registeredWorkflow));
        var now = DateTimeOffset.UtcNow;
        store.WorkflowRuns.Add(new WorkflowRunPersistenceRecord(
            system.Name,
            WorkflowRunId.New(),
            workflowDefinition.Version,
            workflowDefinition.Name,
            Input: null,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkflowRunStatus.Completed,
            [new WorkflowStepPersistenceRecord(
                "dispatch",
                WorkflowStepKind.DispatchWork,
                WorkflowStepRunStatus.Completed,
                WorkerIds: [],
                StartedAt: now,
                CompletedAt: now,
                Messages: [])],
            CreatedAt: now,
            StartedAt: now,
            CompletedAt: now,
            Messages: [],
            ChildReceipts: [],
            WorkflowDefinitionFingerprint.Create(registeredWorkflow)));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(StartupFailurePersistenceStore.InjectedFailureMessage, failure.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Equal(1, store.WorkInitializeCalls);
        Assert.Equal(1, store.WorkflowDeleteCalls);

        await system.Start();

        Assert.Equal(WorkSystemState.Started, system.State);
        Assert.Equal(2, store.WorkflowDeleteCalls);
        await system.Stop();
    }

    [Fact]
    public async Task CleanUpAfterAutomaticStartFailsAndAllowRetry()
    {
        var inputFactoryCalls = 0;
        await using var provider = new ServiceCollection()
            .AddWorkableSystem("startup-failure-tests", builder => builder
                .RequireAuthorization(false)
                .AddWork(
                    WorkDefinition.Create("startup.failure.automatic"),
                    SuccessfulWork,
                    configure => configure.WithAutomaticStart(() =>
                    {
                        if (Interlocked.Increment(ref inputFactoryCalls) == 1)
                        {
                            throw new InvalidOperationException(StartupFailurePersistenceStore.InjectedFailureMessage);
                        }

                        return new StartupInput("retry");
                    })))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(WorkSystemState.Stopped, system.State);

        await system.Start();
        await TestEventually.Until(async () =>
            (await system.Query.Workers(new WorkerCriteria(
                DefinitionName: "startup.failure.automatic",
                States: new HashSet<WorkerState> { WorkerState.Completed }))).TotalCount == 1);

        Assert.Equal(WorkSystemState.Started, system.State);
        await system.Stop();
    }

    [Fact]
    public async Task CleanUpAfterStartupSourceFailsAndAllowRetry()
    {
        var source = new FailOnceStartupWorkSource();
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddWorkableSystem("startup-failure-tests", builder => builder
            .RequireAuthorization(false)
            .AddWork(WorkDefinition.Create("startup.failure.source"), SuccessfulWork)
            .AddStartupWorkSource<FailOnceStartupWorkSource>());
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(WorkSystemState.Stopped, system.State);

        await system.Start();
        await TestEventually.Until(async () =>
            (await system.Query.Workers(new WorkerCriteria(
                DefinitionName: "startup.failure.source",
                States: new HashSet<WorkerState> { WorkerState.Completed }))).TotalCount == 1);

        Assert.Equal(2, source.Calls);
        Assert.Equal(WorkSystemState.Started, system.State);
        await system.Stop();
    }

    private static ServiceProvider CreateProvider(
        StartupFailurePersistenceStore store,
        IWorkSystemLifecycleObserver? observer = null,
        WorkDefinition? definition = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkPersistenceStore>(store);
        if (observer is not null)
        {
            services.AddSingleton(observer);
        }

        services.AddWorkableSystem("startup-failure-tests", builder => builder
            .RequireAuthorization(false)
            .AddWork(definition ?? WorkDefinition.Create("startup.failure.work"), SuccessfulWork));
        return services.BuildServiceProvider();
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed record StartupInput(string Value);

    private sealed class FailOnceStartedObserver : IWorkSystemLifecycleObserver
    {
        internal const string InjectedFailureMessage = "Injected SystemStarted failure.";
        private int startedCalls;

        public int StoppedCalls { get; private set; }

        public Task SystemStarted(IWorkSystem system, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this.startedCalls) == 1)
            {
                throw new InvalidOperationException(InjectedFailureMessage);
            }

            return Task.CompletedTask;
        }

        public Task SystemStopping(
            IWorkSystem system,
            WorkOrigin origin,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SystemStopped(IWorkSystem system, CancellationToken cancellationToken = default)
        {
            this.StoppedCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceStartupWorkSource : IStartupWorkSource
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this.calls) == 1)
            {
                throw new InvalidOperationException(StartupFailurePersistenceStore.InjectedFailureMessage);
            }

            return Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
                [StartupWorkRequest.ForName("startup.failure.source")]);
        }
    }

    private sealed class StartupFailurePersistenceStore(WorkQueueDurabilityEntry? entry = null) : IWorkPersistenceStore
    {
        internal const string InjectedFailureMessage = "Injected startup persistence failure.";
        private int failWorkflowListOnce;
        private int failWorkInitializeOnce;
        private int failWorkflowDeleteOnce;
        private int entryClaimed;
        private int workflowListCalls;
        private int workInitializeCalls;
        private int workflowDeleteCalls;

        public bool FailWorkflowListOnce
        {
            init => this.failWorkflowListOnce = value ? 1 : 0;
        }

        public bool FailWorkInitializeOnce
        {
            init => this.failWorkInitializeOnce = value ? 1 : 0;
        }

        public bool FailWorkflowDeleteOnce
        {
            init => this.failWorkflowDeleteOnce = value ? 1 : 0;
        }

        public int WorkflowListCalls => Volatile.Read(ref this.workflowListCalls);

        public int WorkInitializeCalls => Volatile.Read(ref this.workInitializeCalls);

        public int WorkflowDeleteCalls => Volatile.Read(ref this.workflowDeleteCalls);

        public ConcurrentQueue<string> Operations { get; } = new();

        public List<WorkflowRunPersistenceRecord> WorkflowRuns { get; } = [];

        public Task Initialize(
            WorkQueueDurabilityInitializationContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.workInitializeCalls);
            this.Operations.Enqueue("work-initialize");
            if (Interlocked.Exchange(ref this.failWorkInitializeOnce, 0) == 1)
            {
                throw new InvalidOperationException(InjectedFailureMessage);
            }

            return Task.CompletedTask;
        }

        public Task Enqueue(
            WorkQueueDurabilityEnqueueRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReserveIdempotency(
            WorkIdempotencyPersistenceRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (entry is not null && Interlocked.Exchange(ref this.entryClaimed, 1) == 0)
            {
                yield return entry with
                {
                    Lease = entry.Lease with { OwnerId = request.OwnerId },
                };
            }

            await Task.CompletedTask;
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

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.workflowListCalls);
            this.Operations.Enqueue("workflow-list");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref this.failWorkflowListOnce, 0) == 1)
            {
                throw new InvalidOperationException(InjectedFailureMessage);
            }

            foreach (var run in this.WorkflowRuns)
            {
                yield return run;
            }
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.workflowDeleteCalls);
            if (Interlocked.Exchange(ref this.failWorkflowDeleteOnce, 0) == 1)
            {
                throw new InvalidOperationException(InjectedFailureMessage);
            }

            return Task.CompletedTask;
        }
    }
}

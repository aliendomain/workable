using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Queueing")]
public sealed class DurableQueueTests
{
    [Fact]
    public void DurableQueueingDoesNotRequireIdempotency()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                QueueDurability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                },
            });

        Assert.Empty(messages);
    }

    [Fact]
    public void DurableQueueingRequiresPersistenceBackedIdempotency()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                    Storage = WorkIdempotencyStorage.Local,
                },
                QueueDurability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                },
            });

        Assert.Contains(messages, message => message.Code == "workable.configuration.queue_durability.idempotency_persistence_required");
    }

    [Fact]
    public void DurableQueueFallbackPollingRequiresAtLeastOneSecond()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                    Storage = WorkIdempotencyStorage.Persistence,
                },
                QueueDurability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                    FallbackPollingInterval = TimeSpan.FromMilliseconds(500),
                },
            });

        Assert.Contains(messages, message => message.Code == "workable.configuration.queue_durability.fallback_polling_interval_too_short");
    }

    [Fact]
    public void QueueDurablyCanConfigureFallbackPolling()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("durable-config"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(3))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        var definition = Assert.Single(system.Catalog.Definitions);

        Assert.True(definition.Configuration.QueueDurability.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(3), definition.Configuration.QueueDurability.FallbackPollingInterval);
    }

    [Fact]
    public void DurableCompletionRequiresPersistence()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                QueueDurability = new WorkQueueDurabilityConfiguration
                {
                    CompleteDurably = true,
                },
            });

        Assert.Contains(messages, message => message.Code == "workable.configuration.queue_durability.durable_completion_requires_persistence");
    }

    [Fact]
    public void DurableCompletionIsNotSupportedForRecurringWork()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                    Storage = WorkIdempotencyStorage.Persistence,
                },
                QueueDurability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                    CompleteDurably = true,
                },
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
            });

        Assert.Contains(messages, message => message.Code == "workable.configuration.queue_durability.durable_completion_recurring_not_supported");
    }

    [Fact]
    public async Task DurableQueuePersistsBeforeWorkerStarts()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable", "Runs from durable queue.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (context, input, cancellationToken) =>
                {
                    ran.TrySetResult();
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "42"));
        var handle = await system.Queue.Enqueue("durable", input);

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Single(store.Enqueued);

        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await store.WaitForDeletedFinalWorker(RequiredWorkerId(handle), TimeSpan.FromSeconds(2));

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Contains(RequiredWorkerId(handle).Value, store.DeletedFinalWorkers.Select(id => id.Value));
    }

    [Fact]
    public async Task DurableQueueFlushesFinalCleanupDuringStop()
    {
        var store = new InMemoryDurableQueueStore
        {
            BlockDeleteFinal = true,
        };
        var definition = WorkDefinition.Create("durable-stop-cleanup", "Flushes cleanup on stop.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-stop-cleanup",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "stop-cleanup")));
        var workerId = RequiredWorkerId(handle);
        await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await store.WaitForDeleteFinalStarted(TimeSpan.FromSeconds(2));

        var stop = system.Stop(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(stop.IsCompleted);

        store.ReleaseDeleteFinal.SetResult();
        await stop;

        Assert.Contains(workerId, store.DeletedFinalWorkers);
    }

    [Fact]
    public async Task DurableCompletionCommitsCompletionWithExecutionTransaction()
    {
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-complete", "Completes in the durable transaction.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                async (context, _, cancellationToken) =>
                {
                    await context.CompleteDurably(new TestQueueDurabilityTransaction(), cancellationToken);
                    return WorkExecutionResult.Success();
                },
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()
                    .CompleteDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete")));
        var workerId = RequiredWorkerId(handle);
        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, store.TransactionalDeleteFinals);
        Assert.Contains(workerId, store.DeletedFinalWorkers);
    }

    [Fact]
    public async Task DurableCompletionCanUsePersistenceBackedIdempotencyWithoutDurableQueue()
    {
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-complete-idempotency", "Completes an idempotency row.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                async (context, _, cancellationToken) =>
                {
                    await context.CompleteDurably(new TestQueueDurabilityTransaction(), cancellationToken);
                    return WorkExecutionResult.Success();
                },
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .CompleteDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete-idempotency",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete-idempotency")));
        var workerId = RequiredWorkerId(handle);
        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(0, store.EnqueueAttempts);
        Assert.Equal(1, store.IdempotencyAttempts);
        Assert.Equal(1, store.TransactionalDeleteFinals);
        Assert.Contains(workerId, store.DeletedFinalWorkers);
    }

    [Fact]
    public async Task DurableCompletionThrowsWhenWorkerIsNotConfiguredForDurableCompletion()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-complete-not-configured", "Rejects unexpected durable completion.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                async (context, _, cancellationToken) =>
                {
                    try
                    {
                        await context.CompleteDurably(new TestQueueDurabilityTransaction(), cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        observed.TrySetResult(exception);
                    }

                    return WorkExecutionResult.Success();
                },
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete-not-configured",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete-not-configured")));
        var workerId = RequiredWorkerId(handle);
        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await store.WaitForDeletedFinalWorker(workerId, TimeSpan.FromSeconds(2));

        var exception = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(0, store.TransactionalDeleteFinals);
    }

    [Fact]
    public async Task DurableCompletionFailsWhenSuccessfulExecutionDoesNotCompleteDurably()
    {
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-complete-missing", "Requires explicit durable completion.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()
                    .CompleteDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete-missing",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete-missing")));
        var workerId = RequiredWorkerId(handle);
        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await store.WaitForRetainedFailedWorker(workerId, TimeSpan.FromSeconds(2));

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(0, store.TransactionalDeleteFinals);
        Assert.Contains(workerId, store.RetainedFailedWorkers);
    }

    [Fact]
    public async Task DurableCompletionRetainsDurableRowWhenExecutionFails()
    {
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-complete-failure", "Rolls back durable completion.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                    [WorkMessage.Error("durable.complete.failed", "Nope.")])),
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()
                    .CompleteDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete-failure",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete-failure")));
        var workerId = RequiredWorkerId(handle);
        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await store.WaitForRetainedFailedWorker(workerId, TimeSpan.FromSeconds(2));

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(0, store.TransactionalDeleteFinals);
        Assert.DoesNotContain(workerId, store.DeletedFinalWorkers);
        Assert.Contains(workerId, store.RetainedFailedWorkers);
    }

    [Fact]
    public async Task DurableQueueSignalsReaderAfterOwnTransactionEnqueue()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-signal", "Signals after durable queue.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) =>
                {
                    ran.TrySetResult();
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(5))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        store.ResetClaimReadyAttempts();

        var handle = await system.Queue.Enqueue("durable-signal");
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(store.ClaimReadyAttempts < 3);
    }

    [Fact]
    public async Task DurableQueueDoesNotSignalReaderForCallerTransaction()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-external-transaction", "Does not signal for caller transaction.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) =>
                {
                    ran.TrySetResult();
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        store.ResetClaimReadyAttempts();

        var handle = await system.Queue.Enqueue(
            "durable-external-transaction",
            options: WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() });
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(ran.Task.IsCompleted);
        Assert.Equal(0, store.ClaimReadyAttempts);

        var completion = await handle.WaitForCompletion(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
        await system.Stop();

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueRetainsFailedWorkerUntilCanceledOrCompleted()
    {
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-fails", "Fails from durable queue.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                    [WorkMessage.Error("test.failure", "The durable worker failed.")])),
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-fails",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "failed-retention")));
        var workerId = RequiredWorkerId(handle);
        await store.WaitForRetainedFailedWorker(workerId, TimeSpan.FromSeconds(2));

        var failed = await system.Query.Worker(workerId);
        Assert.NotNull(failed);
        var cancel = await system.Workers.Execute(new WorkerVersion(workerId, failed.Revision), WorkAction.Cancel);
        await store.WaitForDeletedFinalWorker(workerId, TimeSpan.FromSeconds(2));

        Assert.Equal(WorkerState.Failed, failed.State);
        Assert.Contains(workerId, store.RetainedFailedWorkers);
        Assert.True(cancel.IsAccepted);
        Assert.Contains(workerId, store.DeletedFinalWorkers);
    }

    [Fact]
    public async Task DurableIdempotencyIsCheckedByPersistenceProvider()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-idempotent", "Uses provider idempotency.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                async (context, input, cancellationToken) =>
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                },
                configuration => configuration
                    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "provider-owned"));
        var first = await system.Queue.Enqueue("durable-idempotent", input);
        await system.WaitForWorkerState(RequiredWorkerId(first), WorkerState.Running);

        var duplicate = await system.Queue.Enqueue("durable-idempotent", input);

        release.SetResult();
        await first.WaitForCompletion();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Equal(2, store.EnqueueAttempts);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.queue_durability.duplicate");
    }

    [Fact]
    public async Task PersistentIdempotencyWithoutDurableQueueUsesPersistenceProvider()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("persistent-idempotent", "Uses persistence-backed idempotency.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                async (context, input, cancellationToken) =>
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                },
                configuration => configuration.RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotency-only"));
        var first = await system.Queue.Enqueue("persistent-idempotent", input);
        await system.WaitForWorkerState(RequiredWorkerId(first), WorkerState.Running);

        var duplicate = await system.Queue.Enqueue("persistent-idempotent", input);

        release.SetResult();
        await first.WaitForCompletion();
        await store.WaitForDeletedFinalWorker(RequiredWorkerId(first), TimeSpan.FromSeconds(2));

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Empty(store.Enqueued);
        Assert.Equal(2, store.IdempotencyAttempts);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.idempotency.duplicate_subject");
    }

    [Fact]
    public async Task PersistentIdempotencyWithoutDurableQueueRejectsCallerOwnedTransaction()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("persistent-idempotent-transaction", "Rejects queue transactions without durable queueing.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) =>
                {
                    ran.TrySetResult();
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration.RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotency-transaction"));
        var handle = await system.Queue.Enqueue(
            "persistent-idempotent-transaction",
            input,
            WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() });

        await system.Stop();

        Assert.False(handle.QueueOutcome.IsAccepted);
        Assert.Null(handle.WorkerId);
        Assert.False(ran.Task.IsCompleted);
        Assert.Equal(0, store.IdempotencyAttempts);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Code == "workable.idempotency.persistence_transaction_requires_durable_queue");
    }

    [Fact]
    public async Task StartupDrainRetriesAfterClaimFailure()
    {
        var store = new InMemoryDurableQueueStore
        {
            ClaimReadyFailuresRemaining = 1,
        };
        var definition = WorkDefinition.Create("startup-drain-retry", "Retries startup drain.");
        var workerId = WorkerId.New();
        await store.Enqueue(CreateDurableRequest(definition, workerId));
        List<WorkQueueDurabilityEntry> accepted = [];
        var coordinator = CreateCoordinator(
            store,
            (entry, _) =>
            {
                accepted.Add(entry);
                return Task.CompletedTask;
            });

        await coordinator.InitializeAndDrain([definition], new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

        Assert.Single(accepted);
        Assert.Equal(workerId, accepted[0].Lease.WorkerId);
        Assert.True(store.ClaimReadyAttempts >= 2);
    }

    [Fact]
    public async Task BackgroundReaderRetriesAfterClaimFailure()
    {
        using var lifetime = new CancellationTokenSource();
        var accepted = new TaskCompletionSource<WorkQueueDurabilityEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore
        {
            ClaimReadyFailuresRemaining = 1,
        };
        var definition = WorkDefinition.Create("reader-retry", "Retries reader.");
        var workerId = WorkerId.New();
        await store.Enqueue(CreateDurableRequest(definition, workerId));
        var coordinator = CreateCoordinator(
            store,
            (entry, _) =>
            {
                accepted.TrySetResult(entry);
                return Task.CompletedTask;
            },
            lifetime.Token);

        coordinator.StartBackgroundTasks();
        var entry = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Cancel();

        Assert.Equal(workerId, entry.Lease.WorkerId);
        Assert.True(store.ClaimReadyAttempts >= 2);
    }

    [Fact]
    public async Task LeaseRenewalRetriesAfterRenewFailure()
    {
        using var lifetime = new CancellationTokenSource();
        var store = new InMemoryDurableQueueStore
        {
            RenewLeaseFailuresRemaining = 1,
        };
        var workerId = WorkerId.New();
        var coordinator = CreateCoordinator(store, (_, _) => Task.CompletedTask, lifetime.Token);
        coordinator.TrackLease(workerId, new WorkQueueDurabilityLease(workerId, "test-owner", "test-lease"));

        coordinator.StartBackgroundTasks();
        await store.WaitForRenewLeaseAttempts(2, TimeSpan.FromSeconds(2));
        lifetime.Cancel();

        Assert.True(store.RenewLeaseAttempts >= 2);
    }

    [Fact]
    public async Task LeaseRenewalReportsLostLease()
    {
        using var lifetime = new CancellationTokenSource();
        var store = new InMemoryDurableQueueStore
        {
            LoseLeaseOnRenew = true,
        };
        var workerId = WorkerId.New();
        var lostLease = new TaskCompletionSource<WorkerId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = CreateCoordinator(
            store,
            (_, _) => Task.CompletedTask,
            lifetime.Token,
            lostLease.SetResult);
        coordinator.TrackLease(workerId, new WorkQueueDurabilityLease(workerId, "test-owner", "lost-lease"));

        coordinator.StartBackgroundTasks();
        var lostWorkerId = await lostLease.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Cancel();

        Assert.Equal(workerId, lostWorkerId);
    }

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

    private static WorkQueueDurabilityCoordinator CreateCoordinator(
        InMemoryDurableQueueStore store,
        Func<WorkQueueDurabilityEntry, CancellationToken, Task> acceptPersistedEntry,
        CancellationToken lifetimeToken = default,
        Action<WorkerId>? leaseLost = null)
        => new(
            store,
            WorkSystemId.New(),
            "durable-tests",
            () => !lifetimeToken.IsCancellationRequested,
            () => lifetimeToken,
            acceptPersistedEntry,
            leaseLost ?? (_ => { }),
            readerPollInterval: TimeSpan.FromMilliseconds(10),
            leaseRenewalInterval: TimeSpan.FromMilliseconds(10),
            retryDelay: TimeSpan.FromMilliseconds(10),
            readerSignalDebounce: TimeSpan.FromMilliseconds(10),
            leaseDuration: TimeSpan.FromSeconds(1),
            batchSize: 10);

    private static WorkQueueDurabilityEnqueueRequest CreateDurableRequest(
        WorkDefinition definition,
        WorkerId workerId)
        => new(
            WorkSystemId.New(),
            "durable-tests",
            workerId,
            definition,
            WorkInput.Empty,
            WorkerOptions.Default,
            WorkConfiguration.Default,
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Test durable queue request."),
            DateTimeOffset.UtcNow,
            Idempotency: null,
            Transaction: null);

    private sealed class InMemoryDurableQueueStore : IWorkPersistenceStore
    {
        private readonly Lock sync = new();
        private readonly Queue<WorkQueueDurabilityEnqueueRequest> pending = [];
        private readonly Dictionary<WorkerId, WorkQueueDurabilityIdempotency> idempotencyByWorker = [];
        private readonly HashSet<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId)> activeIdempotency = [];

        public List<WorkQueueDurabilityEnqueueRequest> Enqueued { get; } = [];

        public List<WorkerId> DeletedFinalWorkers { get; } = [];

        public List<WorkerId> RetainedFailedWorkers { get; } = [];

        public int EnqueueAttempts { get; private set; }

        public int IdempotencyAttempts { get; private set; }

        public int ClaimReadyFailuresRemaining { get; set; }

        public int ClaimReadyAttempts { get; private set; }

        public int RenewLeaseFailuresRemaining { get; set; }

        public int RenewLeaseAttempts { get; private set; }

        public bool LoseLeaseOnRenew { get; set; }

        public bool BlockDeleteFinal { get; set; }

        public int TransactionalDeleteFinals { get; private set; }

        public TaskCompletionSource DeleteFinalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDeleteFinal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void ResetClaimReadyAttempts()
        {
            lock (this.sync)
            {
                this.ClaimReadyAttempts = 0;
            }
        }

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.EnqueueAttempts++;
                if (request.Idempotency is { } idempotency &&
                    !this.activeIdempotency.Add((request.Definition.Id, idempotency.SubjectId)))
                {
                    throw new WorkQueueDurabilityDuplicateException("Duplicate durable idempotent work.");
                }

                if (request.Idempotency is { } active)
                {
                    this.idempotencyByWorker[request.WorkerId] = active;
                }

                this.Enqueued.Add(request);
                this.pending.Enqueue(request);
            }

            return Task.CompletedTask;
        }

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.IdempotencyAttempts++;
                if (!this.activeIdempotency.Add((request.Definition.Id, request.SubjectId)))
                {
                    throw new WorkQueueDurabilityDuplicateException("Duplicate persistent idempotent work.");
                }

                this.idempotencyByWorker[request.WorkerId] = new WorkQueueDurabilityIdempotency(request.SubjectId);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<WorkQueueDurabilityEnqueueRequest> claimed = [];
            lock (this.sync)
            {
                this.ClaimReadyAttempts++;
                if (this.ClaimReadyFailuresRemaining > 0)
                {
                    this.ClaimReadyFailuresRemaining--;
                    throw new InvalidOperationException("Transient durable claim failure.");
                }

                while (this.pending.TryDequeue(out var entry))
                {
                    claimed.Add(entry);
                }
            }

            foreach (var entry in claimed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new WorkQueueDurabilityEntry(
                    new WorkQueueDurabilityLease(entry.WorkerId, request.OwnerId, Guid.NewGuid().ToString("N")),
                    entry.Definition.Name,
                    entry.Input,
                    entry.Options,
                    entry.Configuration,
                    entry.Origin,
                    entry.CreatedAt);
            }

            await Task.CompletedTask;
        }

        public Task RenewLeases(IReadOnlyList<WorkQueueDurabilityLease> leases, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.RenewLeaseAttempts++;
                if (this.LoseLeaseOnRenew && leases.Count > 0)
                {
                    throw new WorkQueueDurabilityLeaseLostException(leases[0]);
                }

                if (this.RenewLeaseFailuresRemaining > 0)
                {
                    this.RenewLeaseFailuresRemaining--;
                    throw new InvalidOperationException("Transient durable lease renewal failure.");
                }
            }

            return Task.CompletedTask;
        }

        public Task RetainFailed(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.RetainedFailedWorkers.AddRange(workers.Select(worker => worker.WorkerId));
            }

            return Task.CompletedTask;
        }

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
        {
            if (this.BlockDeleteFinal)
            {
                this.DeleteFinalStarted.TrySetResult();
                return this.DeleteFinalAfterRelease(workers, cancellationToken);
            }

            lock (this.sync)
            {
                foreach (var worker in workers)
                {
                    if (this.idempotencyByWorker.Remove(worker.WorkerId, out var idempotency))
                    {
                        var request = this.Enqueued.FirstOrDefault(request => request.WorkerId == worker.WorkerId);
                        if (request is not null)
                        {
                            this.activeIdempotency.Remove((request.Definition.Id, idempotency.SubjectId));
                        }
                    }

                    this.DeletedFinalWorkers.Add(worker.WorkerId);
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.TransactionalDeleteFinals++;
            }

            return this.DeleteFinal(workers, cancellationToken);
        }

        private async Task DeleteFinalAfterRelease(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken)
        {
            await this.ReleaseDeleteFinal.Task.WaitAsync(cancellationToken);
            lock (this.sync)
            {
                foreach (var worker in workers)
                {
                    if (this.idempotencyByWorker.Remove(worker.WorkerId, out var idempotency))
                    {
                        var request = this.Enqueued.FirstOrDefault(request => request.WorkerId == worker.WorkerId);
                        if (request is not null)
                        {
                            this.activeIdempotency.Remove((request.Definition.Id, idempotency.SubjectId));
                        }
                    }

                    this.DeletedFinalWorkers.Add(worker.WorkerId);
                }
            }
        }

        public async Task WaitForDeletedFinalWorker(WorkerId workerId, TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            while (!timeoutCancellation.IsCancellationRequested)
            {
                lock (this.sync)
                {
                    if (this.DeletedFinalWorkers.Contains(workerId))
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutCancellation.Token);
            }
        }

        public async Task WaitForRetainedFailedWorker(WorkerId workerId, TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            while (!timeoutCancellation.IsCancellationRequested)
            {
                lock (this.sync)
                {
                    if (this.RetainedFailedWorkers.Contains(workerId))
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutCancellation.Token);
            }
        }

        public async Task WaitForRenewLeaseAttempts(int minimumAttempts, TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            while (!timeoutCancellation.IsCancellationRequested)
            {
                lock (this.sync)
                {
                    if (this.RenewLeaseAttempts >= minimumAttempts)
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutCancellation.Token);
            }
        }

        public async Task WaitForDeleteFinalStarted(TimeSpan timeout)
            => await this.DeleteFinalStarted.Task.WaitAsync(new CancellationTokenSource(timeout).Token);
    }

    private sealed class TestQueueDurabilityTransaction : IWorkQueueDurabilityTransaction;
}

internal static class DurableQueueTestExtensions
{
    public static async Task WaitForWorkerState(
        this IWorkSystem system,
        WorkerId workerId,
        WorkerState state,
        TimeSpan? timeoutAfter = null)
    {
        using var timeout = new CancellationTokenSource(timeoutAfter ?? TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var current = await system.Query.Worker(workerId, timeout.Token);
            if (current?.State == state)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}

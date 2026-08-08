using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                    },
                },
            });

        Assert.Empty(messages);
    }

    [Fact]
    public void DurableQueueingRequiresPersistentCoordination()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                    },
                },
            });

        Assert.Contains(messages, message => message.Code == "workable.configuration.coordination.durability_requires_persistent_storage");
    }

    [Fact]
    public void DurableQueueFallbackPollingRequiresAtLeastOneSecond()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Idempotency = new WorkIdempotencyConfiguration
                    {
                        IsEnabled = true,
                    },
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                        FallbackPollingInterval = TimeSpan.FromMilliseconds(500),
                    },
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

        Assert.True(definition.Configuration.Coordination.IsDurabilityEnabled);
        Assert.Equal(TimeSpan.FromSeconds(3), definition.Configuration.Coordination.Durability.FallbackPollingInterval);
    }

    [Fact]
    public void DurableCompletionRequiresPersistence()
    {
        var messages = WorkConfigurationValidator.Validate(
            WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        CompleteDurably = true,
                    },
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
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Idempotency = new WorkIdempotencyConfiguration
                    {
                        IsEnabled = true,
                    },
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                        CompleteDurably = true,
                    },
                },
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
            });

        Assert.Contains(messages, message => message.Code == "workable.configuration.queue_durability.durable_completion_recurring_not_supported");
    }

    [Fact]
    public async Task QueueRejectsPersistentCoordinationWhenNoPersistenceStoreIsRegistered()
    {
        var definition = WorkDefinition.Create("persistent-no-store", "Rejects persistent coordination without a store.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration
                    .CoordinatePersistently()
                    .RejectDuplicateSubjects()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "persistent-no-store",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "no-store")));

        Assert.False(handle.QueueOutcome.IsAccepted);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.configuration.coordination.persistence_store_required" &&
            message.Target == "configuration.coordination.storage");
    }

    [Fact]
    public async Task DefinitionReconfigurationRejectsPersistentCoordinationWhenNoPersistenceStoreIsRegistered()
    {
        var definition = WorkDefinition.Create("persistent-reconfigure-no-store", "Rejects persistent definition reconfiguration without a store.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var outcome = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(Configuration: WorkConfiguration.Default with
            {
                Coordination = PersistentIdempotencyCoordination(),
            }));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.coordination.persistence_store_required" &&
            message.Target == "configuration.coordination.storage");
    }

    [Fact]
    public async Task WorkerReconfigurationRejectsPersistentCoordinationWhenNoPersistenceStoreIsRegistered()
    {
        var definition = WorkDefinition.Create(
            "worker-persistent-reconfigure-no-store",
            "Rejects persistent worker reconfiguration without a store.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var handle = await system.Queue.Enqueue("worker-persistent-reconfigure-no-store");
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Coordination: PersistentIdempotencyCoordination()));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.coordination.persistence_store_required" &&
            message.Target == "configuration.coordination.storage");
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
                    .CoordinatePersistently().RejectDuplicateSubjects()
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "42"));
        var handle = await system.Queue.Enqueue("durable", input);

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Single(store.Enqueued);

        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(5));
        await store.WaitForDeletedFinalWorker(RequiredWorkerId(handle), TimeSpan.FromSeconds(2));

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Contains(RequiredWorkerId(handle).Value, store.DeletedFinalWorkers.Select(id => id.Value));
    }

    [Fact]
    public async Task PausedDurableWorkerReplayMaterializesQueuedState()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create(
            "durable-paused-replay",
            "Replays a paused durable worker as queued after restart.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                async (context, input, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        running.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return WorkExecutionResult.Success();
                },
                configuration => configuration
                    .CoordinatePersistently().RejectDuplicateSubjects()
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-paused-replay",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "paused-replay")));
        var workerId = RequiredWorkerId(handle);
        await system.WaitForWorkerState(workerId, WorkerState.Queued);
        var queuedWorker = RequiredWorker(await system.Query.Worker(workerId));

        var firstStart = await system.Workers.Execute(queuedWorker.Version, WorkAction.Start);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));
        var pause = await system.Workers.Execute(runningWorker.Version, WorkAction.Pause);
        var paused = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));

        Assert.True(firstStart.IsAccepted);
        Assert.True(pause.IsAccepted);
        Assert.Equal(WorkerState.Pausing, pause.Worker?.State);
        Assert.Equal(WorkCompletionStatus.Paused, paused.Status);
        Assert.Equal(WorkerState.Paused, paused.Worker?.State);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.DoesNotContain(workerId, store.DeletedFinalWorkers);

        await system.Stop();
        store.Requeue(workerId);
        await system.Start();
        await system.WaitForWorkerState(workerId, WorkerState.Queued);

        var replayedWorker = RequiredWorker(await system.Query.Worker(workerId));
        var replayStart = await system.Workers.Execute(replayedWorker.Version, WorkAction.Start);
        var completed = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));
        await store.WaitForDeletedFinalWorker(workerId, TimeSpan.FromSeconds(2));

        Assert.Equal(WorkerState.Queued, replayedWorker.State);
        Assert.True(replayStart.IsAccepted);
        Assert.True(completed.IsCompletedSuccessfully);
        Assert.Equal(2, Volatile.Read(ref attempts));
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
                    .CoordinatePersistently().RejectDuplicateSubjects()
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-stop-cleanup",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "stop-cleanup")));
        var workerId = RequiredWorkerId(handle);
        await WaitForCompletion(handle, TimeSpan.FromSeconds(2));
        await store.WaitForDeleteFinalStarted(TimeSpan.FromSeconds(2));

        var stop = StopWithTimeout(system, TimeSpan.FromSeconds(2));

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
                    .CoordinatePersistently().RejectDuplicateSubjects()
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
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));

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
                    .CoordinatePersistently().RejectDuplicateSubjects()
                    .CompleteDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete-idempotency",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete-idempotency")));
        var workerId = RequiredWorkerId(handle);
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));

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
                    .CoordinatePersistently().RejectDuplicateSubjects()
                    .QueueDurably()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "durable-complete-not-configured",
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", "complete-not-configured")));
        var workerId = RequiredWorkerId(handle);
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));
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
                    .CoordinatePersistently().RejectDuplicateSubjects()
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
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));
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
                    .CoordinatePersistently().RejectDuplicateSubjects()
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
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(2));
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
        await WaitForCompletion(handle, TimeSpan.FromSeconds(2));
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(store.ClaimReadyAttempts < 3);
    }

    [Fact]
    public async Task DurableQueueProcessesCallerTransactionWorkThroughFallbackPolling()
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

        var handle = await system.Queue.Enqueue(
            "durable-external-transaction",
            options: WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() });

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(3));
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(ran.Task.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueProcessesCallerTransactionWorkAfterExplicitNotification()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryDurableQueueStore();
        var definition = WorkDefinition.Create("durable-explicit-notification", "Wakes after a caller-owned transaction commits.");
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

        var handle = await system.Queue.Enqueue(
            "durable-explicit-notification",
            options: WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() });
        system.Queue.NotifyDurableWorkAvailable();

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var completion = await WaitForCompletion(handle, TimeSpan.FromSeconds(1));
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(ran.Task.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitingForCallerTransactionWorkerDoesNotReduceFallbackPollingInterval()
    {
        var store = new InMemoryDurableQueueStore
        {
            HoldClaims = true,
        };
        var definition = WorkDefinition.Create("durable-waiter-poll", "Keeps the configured fallback interval while waiting.");
        var system = new ServiceCollection()
            .AddSingleton<IWorkPersistenceStore>(store)
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(5))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        store.ResetClaimReadyAttempts();
        var handle = await system.Queue.Enqueue(
            "durable-waiter-poll",
            options: WorkerOptions.Default with { QueueDurabilityTransaction = new TestQueueDurabilityTransaction() });
        using var waitCancellation = new CancellationTokenSource();
        var waiting = handle.WaitForCompletion(waitCancellation.Token);
        await TestEventually.Until(
            () => store.ClaimReadyAttempts == 1,
            "Expected the waiter to issue one immediate reader notification.",
            timeout: TimeSpan.FromSeconds(1));

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, store.ClaimReadyAttempts);
        waitCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await system.Stop();
    }

    [Fact]
    public void RepeatedDurableReaderNotificationsAreCoalesced()
    {
        var coordinator = CreateCoordinator(
            new InMemoryDurableQueueStore(),
            (_, _) => Task.CompletedTask);

        for (var index = 0; index < 100; index++)
        {
            coordinator.SignalReader();
        }

        Assert.Equal(1, coordinator.ReaderSignals);
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
                    .CoordinatePersistently().RejectDuplicateSubjects()
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
                    .CoordinatePersistently().RejectDuplicateSubjects()
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
                configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects()))
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
                configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects()))
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

        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.InitializeAndDrain([definition], drainTimeout.Token);

        Assert.Single(accepted);
        Assert.Equal(workerId, accepted[0].Lease.WorkerId);
        Assert.True(store.ClaimReadyAttempts >= 2);
    }

    [Fact]
    public async Task InitializationFailureDoesNotPreventStartupDrain()
    {
        var store = new InMemoryDurableQueueStore
        {
            InitializeFailuresRemaining = 1,
        };
        var definition = WorkDefinition.Create("init-failure-drain", "Still drains after an initialization failure.");
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

        await coordinator.InitializeAndDrain([definition], CancellationToken.None);

        Assert.Equal(1, store.InitializeAttempts);
        Assert.Single(accepted);
        Assert.Equal(workerId, accepted[0].Lease.WorkerId);
        Assert.True(store.ClaimReadyAttempts >= 1);
    }

    [Fact]
    public async Task StartupDrainUnavailabilityDoesNotBlockStartup()
    {
        var logger = new TestLogger();
        var store = new InMemoryDurableQueueStore
        {
            ClaimReadyUnavailableFailuresRemaining = 1,
        };
        var definition = WorkDefinition.Create("startup-drain-unavailable", "Continues startup when the initial drain cannot reach persistence.");
        var coordinator = CreateCoordinator(
            store,
            (_, _) => Task.CompletedTask,
            logger: logger);

        await coordinator.InitializeAndDrain([definition], CancellationToken.None);

        Assert.Equal(1, store.ClaimReadyAttempts);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                entry.Message.Contains("initializing the persistence store", StringComparison.Ordinal));
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("startup drain could not reach the persistence store", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializationFailureLogsWarningAndRejectsDurableQueueRequestsWhenStoreRemainsUnreachable()
    {
        var logger = new TestLogger();
        var store = new FailingInitializeDurableQueueStore(
            new WorkPersistenceStoreUnavailableException(
                "The test persistence store is unavailable.",
                new InvalidOperationException("LocalDB runtime is unavailable.")));
        var definition = WorkDefinition.Create(
            "durable-store-unavailable",
            "Rejects durable queue requests when the persistence store is unavailable.",
            configuration: WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                    },
                },
            });
        var coordinator = CreateCoordinator(
            store,
            (_, _) => Task.CompletedTask,
            logger: logger);

        await coordinator.InitializeAndDrain([definition], CancellationToken.None);

        var outcome = await coordinator.Enqueue(
            CreateDurableRequest(definition, WorkerId.New()),
            CancellationToken.None);

        Assert.False(outcome.IsAccepted);
        Assert.Contains(
            outcome.Messages,
            message => message.Code == "workable.queue_durability.store_unreachable" &&
                message.Text.Contains("currently unreachable", StringComparison.Ordinal) &&
                message.Text.Contains("The test persistence store is unavailable.", StringComparison.Ordinal));

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                entry.Message.Contains("initializing the persistence store", StringComparison.Ordinal));
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("durable-tests", warning.Message, StringComparison.Ordinal);
        Assert.Contains("The test persistence store is unavailable.", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.InvalidOperationException", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedStoreExceptionIsNotReportedAsPersistenceUnreachable()
    {
        var definition = WorkDefinition.Create(
            "durable-store-bug",
            "Bubbles unexpected store exceptions.",
            configuration: WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                    },
                },
            });
        var expected = new InvalidOperationException("Unexpected store failure.");
        var coordinator = CreateCoordinator(
            new FailingEnqueueDurableQueueStore(expected),
            (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.Enqueue(
                CreateDurableRequest(definition, WorkerId.New()),
                CancellationToken.None));

        Assert.Same(expected, exception);
    }

    [Fact]
    public async Task MissingPersistenceStoreFailsClosedForEveryDurabilityEntryPoint()
    {
        var definition = WorkDefinition.Create("durability.store.required");
        var workerId = WorkerId.New();
        var coordinator = CreateCoordinator(null, (_, _) => Task.CompletedTask);

        await coordinator.InitializeAndDrain([definition], CancellationToken.None);
        var enqueue = await coordinator.Enqueue(
            CreateDurableRequest(definition, workerId),
            CancellationToken.None);
        var reserve = await coordinator.ReserveIdempotency(
            CreateIdempotencyRequest(definition, workerId),
            CancellationToken.None);
        var completionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CompleteDurably(
                workerId,
                new TestQueueDurabilityTransaction(),
                CancellationToken.None));

        Assert.Contains(enqueue.Messages, message => message.Code == "workable.queue_durability.store_required");
        Assert.Contains(reserve.Messages, message => message.Code == "workable.idempotency.persistence_store_required");
        Assert.Contains("registered work persistence store", completionError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyReportsStoreUnavailabilityAndUntrackedCompletion()
    {
        var definition = WorkDefinition.Create("durability.idempotency.unavailable");
        var workerId = WorkerId.New();
        var unavailable = CreateCoordinator(
            new FailingInitializeDurableQueueStore(new WorkPersistenceStoreUnavailableException(
                "offline",
                new InvalidOperationException("offline"))),
            (_, _) => Task.CompletedTask);
        var available = CreateCoordinator(new InMemoryDurableQueueStore(), (_, _) => Task.CompletedTask);

        var reserve = await unavailable.ReserveIdempotency(
            CreateIdempotencyRequest(definition, workerId),
            CancellationToken.None);
        var completionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            available.CompleteDurably(
                workerId,
                new TestQueueDurabilityTransaction(),
                CancellationToken.None));

        Assert.Contains(reserve.Messages, message =>
            message.Code == "workable.idempotency.persistence_store_unreachable" &&
            message.Text.Contains("offline", StringComparison.Ordinal));
        Assert.Contains("persisted durable queue row", completionError.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task TransactionalDurableCompletionReportsAndRemovesALostLease()
    {
        var workerId = WorkerId.New();
        var lease = new WorkQueueDurabilityLease(workerId, "test-owner", "lost-completion-lease");
        var store = new InMemoryDurableQueueStore
        {
            LoseLeaseOnDeleteFinal = lease,
        };
        var lostLease = new TaskCompletionSource<WorkerId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = CreateCoordinator(
            store,
            (_, _) => Task.CompletedTask,
            leaseLost: lostLease.SetResult);
        coordinator.TrackLease(workerId, lease);

        var exception = await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            coordinator.CompleteDurably(workerId, new TestQueueDurabilityTransaction(), CancellationToken.None));

        Assert.Equal(lease, Assert.Single(exception.Leases));
        Assert.Equal(workerId, await lostLease.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CompleteDurably(workerId, new TestQueueDurabilityTransaction(), CancellationToken.None));
    }

    [Fact]
    public async Task CleanupBatchReleasesLostLeaseAndCompletesUnaffectedBookkeeping()
    {
        using var lifetime = new CancellationTokenSource();
        var lostWorkerId = WorkerId.New();
        var unaffectedWorkerId = WorkerId.New();
        var lostLease = new WorkQueueDurabilityLease(lostWorkerId, "test-owner", "lost-cleanup-lease");
        var unaffectedLease = new WorkQueueDurabilityLease(unaffectedWorkerId, "test-owner", "valid-cleanup-lease");
        var store = new InMemoryDurableQueueStore
        {
            LoseLeaseOnDeleteFinal = lostLease,
        };
        var reportedLostLease = new TaskCompletionSource<WorkerId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = CreateCoordinator(
            store,
            (_, _) => Task.CompletedTask,
            lifetime.Token,
            reportedLostLease.SetResult);
        coordinator.TrackLease(lostWorkerId, lostLease);
        coordinator.TrackLease(unaffectedWorkerId, unaffectedLease);

        coordinator.StartBackgroundTasks();
        coordinator.DeleteFinal(lostWorkerId);
        coordinator.DeleteFinal(unaffectedWorkerId);

        Assert.Equal(lostWorkerId, await reportedLostLease.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await TestEventually.Until(
            () => coordinator.Diagnostics.PendingCleanupCount == 0,
            "Expected lease-loss cleanup to settle both lost and unaffected bookkeeping.",
            timeout: TimeSpan.FromSeconds(2));
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.StopBackgroundTasks(stopTimeout.Token);
        lifetime.Cancel();
    }

    [Fact]
    public async Task DiagnosticsReportPendingDurableCleanup()
    {
        using var lifetime = new CancellationTokenSource();
        var store = new InMemoryDurableQueueStore
        {
            BlockDeleteFinal = true,
        };
        var workerId = WorkerId.New();
        var coordinator = CreateCoordinator(store, (_, _) => Task.CompletedTask, lifetime.Token);
        coordinator.TrackLease(workerId, new WorkQueueDurabilityLease(workerId, "test-owner", "cleanup-lease"));

        coordinator.StartBackgroundTasks();
        coordinator.DeleteFinal(workerId);
        await store.WaitForDeleteFinalStarted(TimeSpan.FromSeconds(2));
        var diagnostics = coordinator.Diagnostics;

        Assert.Equal(1, diagnostics.PendingCleanupCount);
        Assert.True(diagnostics.OldestPendingCleanupAge >= TimeSpan.Zero);

        store.ReleaseDeleteFinal.TrySetResult();
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.StopBackgroundTasks(stopTimeout.Token);
        lifetime.Cancel();
    }

    [Fact]
    public async Task DiagnosticsReportAcceptedWorkerWaiters()
    {
        var store = new InMemoryDurableQueueStore();
        var coordinator = CreateCoordinator(store, (_, _) => Task.CompletedTask);
        var workerId = WorkerId.New();
        var outcome = WorkQueueOutcome.Accepted(workerId);
        var handle = coordinator.CreateHandle(outcome, _ => null);
        using var waitCancellation = new CancellationTokenSource();
        var waiting = Task.Run(async () =>
        {
            try
            {
                await handle.WaitForCompletion(waitCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when waitCancellation is canceled.
            }
        });

        await TestEventually.Until(
            () => coordinator.Diagnostics.AcceptedWaiterCount == 1,
            "Expected the accepted worker waiter count to be tracked.",
            timeout: TimeSpan.FromSeconds(2));
        var diagnostics = coordinator.Diagnostics;

        Assert.Equal(1, diagnostics.AcceptedWaiterCount);
        Assert.True(diagnostics.OldestAcceptedWaiterAge >= TimeSpan.Zero);

        waitCancellation.Cancel();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker.");

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

    private static async Task<WorkCompletion> WaitForCompletion(IWorkerHandle handle, TimeSpan timeoutAfter)
    {
        using var timeout = new CancellationTokenSource(timeoutAfter);
        return await handle.WaitForCompletion(timeout.Token);
    }

    private static async Task StopWithTimeout(IWorkSystem system, TimeSpan timeoutAfter)
    {
        using var timeout = new CancellationTokenSource(timeoutAfter);
        await system.Stop(timeout.Token);
    }

    private static WorkQueueDurabilityCoordinator CreateCoordinator(
        IWorkPersistenceStore? store,
        Func<WorkQueueDurabilityEntry, CancellationToken, Task> acceptPersistedEntry,
        CancellationToken lifetimeToken = default,
        Action<WorkerId>? leaseLost = null,
        ILogger? logger = null)
        => new(
            store,
            WorkSystemId.New(),
            "durable-tests",
            new WorkSystemIdempotencyDiagnosticsTracker(),
            () => !lifetimeToken.IsCancellationRequested,
            () => lifetimeToken,
            acceptPersistedEntry,
            leaseLost ?? (_ => { }),
            logger,
            readerPollInterval: TimeSpan.FromMilliseconds(10),
            leaseRenewalInterval: TimeSpan.FromMilliseconds(10),
            retryDelay: TimeSpan.FromMilliseconds(10),
            leaseDuration: TimeSpan.FromSeconds(1),
            batchSize: 10);

    private static WorkIdempotencyPersistenceRequest CreateIdempotencyRequest(
        WorkDefinition definition,
        WorkerId workerId)
        => new(
            WorkSystemId.New(),
            "durable-tests",
            workerId,
            definition,
            new WorkSubjectId("test", workerId.Value.ToString("N")),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            Transaction: null);

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
            WorkRequestContext.Create(WorkInvocationChannel.InProcess, description: "Test durable queue request."),
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

        public int InitializeFailuresRemaining { get; set; }

        public int InitializeAttempts { get; private set; }

        public int ClaimReadyFailuresRemaining { get; set; }

        public int ClaimReadyUnavailableFailuresRemaining { get; set; }

        public int ClaimReadyAttempts { get; private set; }

        public bool HoldClaims { get; set; }

        public int RenewLeaseFailuresRemaining { get; set; }

        public int RenewLeaseAttempts { get; private set; }

        public bool LoseLeaseOnRenew { get; set; }

        public WorkQueueDurabilityLease? LoseLeaseOnDeleteFinal { get; set; }

        public bool BlockDeleteFinal { get; set; }

        public int TransactionalDeleteFinals { get; private set; }

        public TaskCompletionSource DeleteFinalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDeleteFinal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.InitializeAttempts++;
                if (this.InitializeFailuresRemaining > 0)
                {
                    this.InitializeFailuresRemaining--;
                    throw new WorkPersistenceStoreUnavailableException(
                        "Transient durable initialization failure.",
                        new InvalidOperationException("Transient durable initialization failure."));
                }
            }

            return Task.CompletedTask;
        }

        public void ResetClaimReadyAttempts()
        {
            lock (this.sync)
            {
                this.ClaimReadyAttempts = 0;
            }
        }

        public void Requeue(WorkerId workerId)
        {
            lock (this.sync)
            {
                var request = this.Enqueued.LastOrDefault(entry => entry.WorkerId == workerId)
                    ?? throw new InvalidOperationException($"Expected durable row for worker '{workerId.Value:D}'.");
                this.pending.Enqueue(request);
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
                if (this.ClaimReadyUnavailableFailuresRemaining > 0)
                {
                    this.ClaimReadyUnavailableFailuresRemaining--;
                    throw new WorkPersistenceStoreUnavailableException(
                        "Transient durable claim unavailability.",
                        new InvalidOperationException("Transient durable claim unavailability."));
                }

                if (this.ClaimReadyFailuresRemaining > 0)
                {
                    this.ClaimReadyFailuresRemaining--;
                    throw new InvalidOperationException("Transient durable claim failure.");
                }

                while (!this.HoldClaims && this.pending.TryDequeue(out var entry))
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
                    entry.RequestContext,
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
            if (this.LoseLeaseOnDeleteFinal is { } lostLease)
            {
                this.LoseLeaseOnDeleteFinal = null;
                throw new WorkQueueDurabilityLeaseLostException(lostLease);
            }

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
            => await TestEventually.Until(
                () =>
                {
                    lock (this.sync)
                    {
                        return this.DeletedFinalWorkers.Contains(workerId);
                    }
                },
                $"Expected durable final cleanup for worker '{workerId.Value:D}'.",
                timeout: timeout);

        public async Task WaitForRetainedFailedWorker(WorkerId workerId, TimeSpan timeout)
            => await TestEventually.Until(
                () =>
                {
                    lock (this.sync)
                    {
                        return this.RetainedFailedWorkers.Contains(workerId);
                    }
                },
                $"Expected durable failed-worker retention for worker '{workerId.Value:D}'.",
                timeout: timeout);

        public async Task WaitForRenewLeaseAttempts(int minimumAttempts, TimeSpan timeout)
            => await TestEventually.Until(
                () =>
                {
                    lock (this.sync)
                    {
                        return this.RenewLeaseAttempts >= minimumAttempts;
                    }
                },
                $"Expected at least {minimumAttempts} lease renewal attempt(s).",
                timeout: timeout);

        public async Task WaitForDeleteFinalStarted(TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            await this.DeleteFinalStarted.Task.WaitAsync(timeoutCancellation.Token);
        }
    }

    private sealed class FailingInitializeDurableQueueStore(Exception exception) : IWorkPersistenceStore
    {
        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task RenewLeases(IReadOnlyList<WorkQueueDurabilityLease> leases, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RetainFailed(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FailingEnqueueDurableQueueStore(Exception exception) : IWorkPersistenceStore
    {
        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task RenewLeases(IReadOnlyList<WorkQueueDurabilityLease> leases, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RetainFailed(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestLogger : ILogger
    {
        public List<TestLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Entries.Add(new TestLogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed record TestLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class TestQueueDurabilityTransaction : IWorkQueueDurabilityTransaction;
}
internal static class DurableQueueTestExtensions
{
    public static async Task WaitForWorkerState(
        this IWorkSystem system,
        WorkerId workerId,
        WorkerState state,
        TimeSpan? timeoutAfter = null)
        => await TestEventually.Until(
            async () =>
            {
                var current = await system.Query.Worker(workerId);
                return current?.State == state;
            },
            $"Expected worker '{workerId.Value:D}' to reach state '{state}'.",
            timeout: timeoutAfter ?? TimeSpan.FromSeconds(5));
}

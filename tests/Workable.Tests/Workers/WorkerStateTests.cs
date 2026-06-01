using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerStateTests
{
    [Fact]
    public async Task PauseMovesThroughPausingAndCanBeStartedAgain()
    {
        var running = CreateSignal();
        var attempts = 0;
        var definition = WorkDefinition.Create("pausable", "Can pause.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                running.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("pausable");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));

        var pause = await system.Workers.Execute(runningWorker.Version, WorkAction.Pause);
        var paused = await handle.WaitForCompletion();
        var pausedWorker = RequiredCompletionWorker(paused);

        Assert.True(pause.IsAccepted);
        Assert.Equal(WorkerState.Pausing, pause.Worker?.State);
        Assert.Equal(WorkCompletionStatus.Paused, paused.Status);
        Assert.Equal(WorkerState.Paused, pausedWorker.State);

        var start = await system.Workers.Execute(pausedWorker.Version, WorkAction.Start);
        var completed = await handle.WaitForCompletion();

        Assert.True(start.IsAccepted);
        Assert.Equal(WorkerState.Running, start.Worker?.State);
        Assert.True(completed.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancelMovesThroughCancelingAndCannotBeStartedAgain()
    {
        var running = CreateSignal();
        var definition = WorkDefinition.Create("cancelable", "Can cancel.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("cancelable");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));

        var cancel = await system.Workers.Execute(runningWorker.Version, WorkAction.Cancel);
        var canceled = await handle.WaitForCompletion();
        var canceledWorker = RequiredCompletionWorker(canceled);
        var restart = await system.Workers.Execute(canceledWorker.Version, WorkAction.Start);

        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkerState.Canceling, cancel.Worker?.State);
        Assert.Equal(WorkCompletionStatus.Canceled, canceled.Status);
        Assert.Equal(WorkerState.Canceled, canceled.Worker?.State);
        Assert.Equal(WorkActionStatus.Invalid, restart.Status);
    }

    [Fact]
    public async Task PauseCancelsExecutorTokenAndCompletesAsPaused()
    {
        var running = CreateSignal();
        var tokenCanceled = CreateSignal();
        var definition = WorkDefinition.Create("pause-token", "Observes pause cancellation.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => tokenCanceled.SetResult());
            running.SetResult();
            await tokenCanceled.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("pause-token");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));

        var pause = await system.Workers.Execute(runningWorker.Version, WorkAction.Pause);
        await tokenCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await handle.WaitForCompletion();

        Assert.True(pause.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Paused, completion.Status);
    }

    [Fact]
    public async Task CancelCancelsExecutorTokenAndCompletesAsCanceled()
    {
        var running = CreateSignal();
        var tokenCanceled = CreateSignal();
        var definition = WorkDefinition.Create("cancel-token", "Observes cancel cancellation.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => tokenCanceled.SetResult());
            running.SetResult();
            await tokenCanceled.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("cancel-token");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));

        var cancel = await system.Workers.Execute(runningWorker.Version, WorkAction.Cancel);
        await tokenCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await handle.WaitForCompletion();

        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task ShutdownInterruptionCancelsExecutorTokenCompletesAsInterruptedAndPublishesEvent()
    {
        var running = CreateSignal();
        var interruptedSeen = CreateSignal();
        var definition = WorkDefinition.Create("shutdown-interrupted", "Observes shutdown interruption.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            running.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorkExecutionResult.Success();
            }
            catch (OperationCanceledException)
            {
                if (context.IsInterrupted)
                {
                    Assert.Equal(WorkInterruptionReason.Shutdown, context.InterruptionReason);
                    interruptedSeen.SetResult();
                }

                throw;
            }
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("shutdown-interrupted");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: workerId, EventType: "worker.interrupted"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        await system.Stop();
        var completion = await handle.WaitForCompletion();
        var interruptedEvent = await ReadNext(reader);

        await interruptedSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkCompletionStatus.Interrupted, completion.Status);
        Assert.Equal(WorkerState.Interrupted, completion.Worker?.State);
        Assert.Equal(WorkInterruptionReason.Shutdown, completion.Worker?.InterruptionReason);
        Assert.Equal("worker.interrupted", interruptedEvent.EventType);
    }

    [Fact]
    public async Task ConcurrentStateChangesConflict()
    {
        var running = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("slow-pause", "Pauses slowly.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            running.SetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await release.Task;
                throw;
            }

            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("slow-pause");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));

        var firstPause = await system.Workers.Execute(runningWorker.Version, WorkAction.Pause);
        var firstPauseWorker = RequiredOutcomeWorker(firstPause);
        var secondPause = await system.Workers.Execute(firstPauseWorker.Version, WorkAction.Pause);
        release.SetResult();
        var paused = await handle.WaitForCompletion();

        Assert.True(firstPause.IsAccepted);
        Assert.Equal(WorkerState.Pausing, firstPause.Worker?.State);
        Assert.Equal(WorkActionStatus.Conflict, secondPause.Status);
        Assert.Equal(WorkCompletionStatus.Paused, paused.Status);
    }

    [Fact]
    public async Task PurgeRemovesFinalWorkerFromMemory()
    {
        var definition = WorkDefinition.Create("purgeable", "Can be purged.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success()));

        await system.Start();

        var handle = await system.Queue.Enqueue("purgeable");
        var completed = await handle.WaitForCompletion();
        var completedWorker = RequiredCompletionWorker(completed);

        var purge = await system.Workers.Execute(completedWorker.Version, WorkAction.Purge);
        var snapshot = await system.Query.Worker(completedWorker.Id);

        Assert.True(purge.IsAccepted);
        Assert.Null(snapshot);
        Assert.DoesNotContain("Purged", Enum.GetNames<WorkerState>());
    }

    [Fact]
    public async Task FailedWorkerIsNotFinalAndCannotBePurged()
    {
        var definition = WorkDefinition.Create("fails", "Fails when executed.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("test.failure", "The work failed.")])));

        await system.Start();

        var handle = await system.Queue.Enqueue("fails");
        var failed = await handle.WaitForCompletion();
        var failedWorker = RequiredCompletionWorker(failed);

        var purge = await system.Workers.Execute(failedWorker.Version, WorkAction.Purge);
        var cancel = await system.Workers.Execute(failedWorker.Version, WorkAction.Cancel);

        Assert.Equal(WorkCompletionStatus.Failed, failed.Status);
        Assert.Equal(WorkerState.Failed, failedWorker.State);
        Assert.Equal(WorkActionStatus.Invalid, purge.Status);
        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkerState.Canceled, cancel.Worker?.State);
    }

    [Fact]
    public async Task CompletedWorkerIsAutomaticallyPurgedAfterPurgeInterval()
    {
        var definition = WorkDefinition.Create("auto-purge", "Purges after completion.",
            configuration: WorkConfiguration.Default with
            {
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMilliseconds(20),
                },
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success()));

        await system.Start();

        var handle = await system.Queue.Enqueue("auto-purge");
        var completed = await handle.WaitForCompletion();
        var completedWorker = RequiredCompletionWorker(completed);

        await TestEventually.Until(async () => await system.Query.Worker(completedWorker.Id) is null);

        Assert.Equal(WorkCompletionStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task CanceledQueuedWorkerIsAutomaticallyPurgedAfterPurgeInterval()
    {
        var definition = WorkDefinition.Create("canceled-auto-purge", "Purges after queued work is canceled.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMilliseconds(20),
                },
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success()));

        await system.Start();

        var handle = await system.Queue.Enqueue("canceled-auto-purge");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));
        var cancel = await system.Workers.Execute(worker.Version, WorkAction.Cancel);

        await TestEventually.Until(async () => await system.Query.Worker(worker.Id) is null);

        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkerState.Canceled, cancel.Worker?.State);
    }

    [Fact]
    public async Task CompletedWorkersArePurgedWhenMaximumFinalWorkersIsExceeded()
    {
        var definition = WorkDefinition.Create("auto-purge-count", "Purges final workers when count is exceeded.",
            configuration: WorkConfiguration.Default with
            {
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMinutes(10),
                    MaximumFinalWorkers = 2,
                },
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success()));

        await system.Start();

        var first = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-count")).WaitForCompletion());
        await TestEventually.ClockAfter(first.CreatedAt);
        var second = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-count")).WaitForCompletion());
        await TestEventually.ClockAfter(second.CreatedAt);
        var third = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-count")).WaitForCompletion());
        var workers = new[] { first, second, third };

        await TestEventually.Until(async () => await system.Query.Worker(first.Id) is null);

        Assert.Equal(2, await CountExistingWorkers(system, workers));
        Assert.Null(await system.Query.Worker(first.Id));
        Assert.NotNull(await system.Query.Worker(second.Id));
        Assert.NotNull(await system.Query.Worker(third.Id));
    }

    [Fact]
    public async Task CompletedWorkersArePurgedWhenSystemMaximumFinalWorkersIsExceeded()
    {
        var firstDefinition = WorkDefinition.Create("auto-purge-system-a", "First definition for system purge cap.");
        var secondDefinition = WorkDefinition.Create("auto-purge-system-b", "Second definition for system purge cap.");
        var system = CreateSystem(builder => builder
            .ConfigureRetention(maximumFinalWorkers: 3)
            .AddWork(firstDefinition, (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success()))
            .AddWork(secondDefinition, (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success())));

        await system.Start();

        var first = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-system-a")).WaitForCompletion());
        await TestEventually.ClockAfter(first.CreatedAt);
        var second = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-system-b")).WaitForCompletion());
        await TestEventually.ClockAfter(second.CreatedAt);
        var third = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-system-a")).WaitForCompletion());
        await TestEventually.ClockAfter(third.CreatedAt);
        var fourth = RequiredCompletionWorker(await (await system.Queue.Enqueue("auto-purge-system-b")).WaitForCompletion());
        var workers = new[] { first, second, third, fourth };

        await TestEventually.Until(async () => await system.Query.Worker(first.Id) is null);

        Assert.Equal(3, await CountExistingWorkers(system, workers));
        Assert.Null(await system.Query.Worker(first.Id));
        Assert.NotNull(await system.Query.Worker(second.Id));
        Assert.NotNull(await system.Query.Worker(third.Id));
        Assert.NotNull(await system.Query.Worker(fourth.Id));
    }

    [Fact]
    public async Task QueueRejectsWhenSystemMaximumWorkersIsReached()
    {
        var definition = WorkDefinition.Create("system-capacity", "Rejects when system worker count reaches the configured limit.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(builder => builder
            .ConfigureCapacity(maximumWorkers: 2)
            .AddWork(definition, (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success())));

        await system.Start();

        var first = await system.Queue.Enqueue("system-capacity");
        var second = await system.Queue.Enqueue("system-capacity");
        var third = await system.Queue.Enqueue("system-capacity");

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Accepted, second.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Invalid, third.QueueOutcome.Status);
        Assert.Contains(third.QueueOutcome.Messages, message =>
            message.Code == "workable.system.capacity_reached" &&
            message.Target == "system.capacity.maximumWorkers");
    }

    [Fact]
    public async Task FinalWorkersDoNotCountAgainstSystemMaximumWorkers()
    {
        var definition = WorkDefinition.Create(
            "system-capacity-final",
            "Retained final workers do not block new queue requests.",
            configuration: WorkConfiguration.Default with
            {
                Start = new WorkStartConfiguration
                {
                    Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
                },
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMinutes(10),
                },
            });
        var system = CreateSystem(builder => builder
            .ConfigureCapacity(maximumWorkers: 1)
            .AddWork(definition, (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success())));

        await system.Start();

        var first = await system.Queue.Enqueue("system-capacity-final");
        var retained = await system.Query.Worker(RequiredWorkerId(first));
        var second = await system.Queue.Enqueue("system-capacity-final");

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkerState.Completed, retained?.State);
        Assert.Equal(WorkQueueStatus.Accepted, second.QueueOutcome.Status);
    }

    [Fact]
    public async Task FailedWorkersCountAgainstSystemMaximumWorkers()
    {
        var definition = WorkDefinition.Create(
            "system-capacity-failed",
            "Failed workers remain non-final and block queue requests at system capacity.");
        var system = CreateSystem(builder => builder
            .ConfigureCapacity(maximumWorkers: 2)
            .AddWork(definition, (context, input, cancellationToken) =>
                Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("test.failure", "The work failed.")]))));

        await system.Start();

        var first = await system.Queue.Enqueue("system-capacity-failed");
        var firstCompletion = await first.WaitForCompletion();
        var second = await system.Queue.Enqueue("system-capacity-failed");
        var secondCompletion = await second.WaitForCompletion();
        var third = await system.Queue.Enqueue("system-capacity-failed");

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Accepted, second.QueueOutcome.Status);
        Assert.Equal(WorkerState.Failed, firstCompletion.Worker?.State);
        Assert.Equal(WorkerState.Failed, secondCompletion.Worker?.State);
        Assert.Equal(WorkQueueStatus.Invalid, third.QueueOutcome.Status);
        Assert.Contains(third.QueueOutcome.Messages, message =>
            message.Code == "workable.system.capacity_reached" &&
            message.Target == "system.capacity.maximumWorkers");
    }

    [Fact]
    public async Task SystemStopResetsApproximateWorkerCapacityCount()
    {
        var definition = WorkDefinition.Create("system-capacity-reset", "Capacity count resets when worker memory is cleared.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(builder => builder
            .ConfigureCapacity(maximumWorkers: 1)
            .AddWork(definition, (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success())));

        await system.Start();

        var first = await system.Queue.Enqueue("system-capacity-reset");
        var rejected = await system.Queue.Enqueue("system-capacity-reset");
        await system.Stop();
        await system.Start();
        var afterRestart = await system.Queue.Enqueue("system-capacity-reset");

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Invalid, rejected.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Accepted, afterRestart.QueueOutcome.Status);
    }

    [Fact]
    public async Task WorkerCanceledDuringSystemStopIsClearedFromMemory()
    {
        var definition = WorkDefinition.Create("stop-clear-memory", "Clears queued work canceled by system stop.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMilliseconds(20),
                },
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success()));

        await system.Start();

        var handle = await system.Queue.Enqueue("stop-clear-memory");
        var workerId = RequiredWorkerId(handle);

        await system.Stop();
        var cleared = await system.Query.Worker(workerId);

        Assert.Null(cleared);
    }

    [Fact]
    public async Task FailedWorkerIsNotScheduledForAutomaticPurge()
    {
        var definition = WorkDefinition.Create("failed-auto-purge", "Failed workers are not final.",
            configuration: WorkConfiguration.Default with
            {
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMilliseconds(20),
                },
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("test.failure", "The work failed.")])));

        await system.Start();

        var handle = await system.Queue.Enqueue("failed-auto-purge");
        var failed = await handle.WaitForCompletion();
        var failedWorker = RequiredCompletionWorker(failed);
        var snapshot = await system.Query.Worker(failedWorker.Id);
        var diagnostics = system.Diagnostics.Retention;

        Assert.Equal(WorkCompletionStatus.Failed, failed.Status);
        Assert.NotNull(snapshot);
        Assert.Equal(WorkerState.Failed, snapshot.State);
        Assert.Equal(0, diagnostics.TrackedFinalWorkerCount);
        Assert.Equal(0, diagnostics.ScheduledPurgeCount);
    }

    [Fact]
    public async Task WorkerActionsCanRejectStaleRevisions()
    {
        var running = CreateSignal();
        var definition = WorkDefinition.Create("versioned", "Tracks revisions.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("versioned");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var worker = RequiredWorker(await system.Query.Worker(workerId));

        Assert.Equal(0, worker.Revision);
        Assert.Equal(1, worker.StateSequence);

        var staleCancel = await system.Workers.Execute(
            new WorkerVersion(workerId, Revision: -1),
            WorkAction.Cancel);
        var acceptedCancel = await system.Workers.Execute(
            worker.Version,
            WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.Conflict, staleCancel.Status);
        Assert.True(acceptedCancel.IsAccepted);
        Assert.Equal(1, acceptedCancel.Worker?.Revision);
        Assert.Equal(2, acceptedCancel.Worker?.StateSequence);
    }

    [Fact]
    public async Task RuntimeProgressDoesNotAdvanceControlRevision()
    {
        var running = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("completes", "Completes after release.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            running.SetResult();
            await release.Task;
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("completes");
        await running.Task;
        var workerId = RequiredWorkerId(handle);
        var runningWorker = RequiredWorker(await system.Query.Worker(workerId));

        release.SetResult();
        var completed = await handle.WaitForCompletion();
        var cancelAfterCompletion = await system.Workers.Execute(runningWorker.Version, WorkAction.Cancel);

        Assert.Equal(0, runningWorker.Revision);
        Assert.Equal(1, runningWorker.StateSequence);
        Assert.Equal(0, completed.Worker?.Revision);
        Assert.Equal(2, completed.Worker?.StateSequence);
        Assert.Equal(WorkActionStatus.Invalid, cancelAfterCompletion.Status);
    }

    [Fact]
    public async Task ReconfigureCanRejectStaleRevisions()
    {
        var definition = WorkDefinition.Create("configurable", "Can be configured.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success()));

        await system.Start();

        var handle = await system.Queue.Enqueue("configurable");
        var workerId = RequiredWorkerId(handle);
        var worker = RequiredWorker(await system.Query.Worker(workerId));

        Assert.Equal(0, worker.Revision);

        var accepted = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(ProfilingEnabled: true),
            cancellationToken: default);
        var conflict = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(ProfilingEnabled: false));

        Assert.True(accepted.IsAccepted);
        Assert.Equal(1, accepted.Worker?.Revision);
        Assert.True(accepted.Worker?.Options.ProfilingEnabled);
        Assert.Equal(WorkActionStatus.Conflict, conflict.Status);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static IWorkSystem CreateSystem(Action<IWorkSystemBuilder> configure)
        => new ServiceCollection()
            .AddWorkableSystem(configure)
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected the queue to accept a worker.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    private static WorkerSnapshot RequiredOutcomeWorker(WorkActionOutcome outcome)
        => outcome.Worker ?? throw new InvalidOperationException("Expected action outcome to include worker.");

    private static WorkerSnapshot RequiredCompletionWorker(WorkCompletion completion)
        => completion.Worker ?? throw new InvalidOperationException("Expected completion to include worker.");

    private static async Task<int> CountExistingWorkers(IWorkSystem system, IEnumerable<WorkerSnapshot> workers)
    {
        var existing = await Task.WhenAll(workers.Select(async worker => await system.Query.Worker(worker.Id) is not null));
        return existing.Count(exists => exists);
    }

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }
}

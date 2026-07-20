using System.Collections.Concurrent;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerRetentionSchedulerShould
{
    [Fact]
    public void TrackOnlyFinalWorkersAndClearAllRetentionBookkeeping()
    {
        var index = new WorkerIndex();
        using var scheduler = new WorkerRetentionScheduler(
            index,
            WorkSystemRetentionConfiguration.Default,
            (_, _) => 0);
        var queued = CreateWorker("retention.queued", final: false);
        scheduler.Schedule(queued);

        Assert.Equal(0, scheduler.Diagnostics.TrackedFinalWorkerCount);
        Assert.Equal(0, scheduler.Diagnostics.ScheduledPurgeCount);

        var final = CreateWorker("retention.final");
        scheduler.Schedule(final);
        Assert.Equal(1, scheduler.Diagnostics.TrackedFinalWorkerCount);
        Assert.Equal(1, scheduler.Diagnostics.ScheduledPurgeCount);

        scheduler.Forget(final.Id);
        Assert.Equal(0, scheduler.Diagnostics.TrackedFinalWorkerCount);
        scheduler.Clear();
        Assert.Equal(0, scheduler.Diagnostics.ScheduledPurgeCount);
        Assert.Equal(0, scheduler.Diagnostics.ScheduledPurgeHighWaterMark);
    }

    [Fact]
    public async Task BatchDueWorkersAcrossDefinitionsWithoutApplyingTheWrongDefinitionScope()
    {
        var index = new WorkerIndex();
        var workers = new Dictionary<WorkerId, WorkerRecord>();
        var calls = new ConcurrentQueue<(IReadOnlyList<WorkerId> WorkerIds, WorkDefinitionId? DefinitionId)>();
        using var scheduler = new WorkerRetentionScheduler(
            index,
            WorkSystemRetentionConfiguration.Default,
            (workerIds, definitionId) =>
            {
                calls.Enqueue((workerIds, definitionId));
                foreach (var workerId in workerIds)
                {
                    index.Forget(workers[workerId]);
                }

                return workerIds.Count;
            });
        var first = CreateWorker("retention.batch.first", purgeInterval: TimeSpan.FromTicks(1));
        var second = CreateWorker("retention.batch.second", purgeInterval: TimeSpan.FromTicks(1));
        workers[first.Id] = first;
        workers[second.Id] = second;
        index.Register(first);
        index.Register(second);
        scheduler.Schedule(first);
        scheduler.Schedule(second);

        scheduler.Start();
        await TestEventually.Until(() => Task.FromResult(!calls.IsEmpty));
        await scheduler.Stop(CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Null(call.DefinitionId);
        Assert.Equal(
            new[] { first.Id, second.Id }.OrderBy(id => id.Value).ToArray(),
            call.WorkerIds.OrderBy(id => id.Value).ToArray());
        Assert.Equal(2, scheduler.Diagnostics.TotalPurgedCount);
    }

    [Fact]
    public async Task PurgeTheOldestFinalWorkerWhenTheSystemCountCapIsExceeded()
    {
        var index = new WorkerIndex();
        var purged = new TaskCompletionSource<(IReadOnlyList<WorkerId>, WorkDefinitionId?)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        var oldest = CreateWorker("retention.system.oldest", createdAt: now);
        var newest = CreateWorker("retention.system.newest", createdAt: now.AddSeconds(1));
        index.Register(oldest);
        index.Register(newest);
        using var scheduler = new WorkerRetentionScheduler(
            index,
            WorkSystemRetentionConfiguration.Default with { MaximumFinalWorkers = 1 },
            (workerIds, definitionId) =>
            {
                foreach (var workerId in workerIds)
                {
                    index.Forget(workerId == oldest.Id ? oldest : newest);
                }

                purged.TrySetResult((workerIds, definitionId));
                return workerIds.Count;
            });
        scheduler.Schedule(oldest);
        scheduler.Schedule(newest);

        scheduler.Start();
        var call = await purged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.Stop(CancellationToken.None);

        Assert.Equal(oldest.Id, Assert.Single(call.Item1));
        Assert.Null(call.Item2);
        Assert.Equal(1, scheduler.Diagnostics.TotalPurgedCount);
    }

    [Fact]
    public async Task DeferredWorkersAreNotImmediatelyReselectedByCountRetention()
    {
        var index = new WorkerIndex();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = CreateWorker("retention.deferred-count-retry");
        index.Register(worker);
        WorkerRetentionScheduler? scheduler = null;
        var attempts = 0;
        scheduler = new WorkerRetentionScheduler(
            index,
            WorkSystemRetentionConfiguration.Default with { MaximumFinalWorkers = 0 },
            (_, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                scheduler!.ScheduleDeferred(worker);
                if (attempt == 1)
                {
                    firstAttempt.TrySetResult();
                }
                else
                {
                    secondAttempt.TrySetResult();
                }

                return 0;
            });
        using (scheduler)
        {
            scheduler.Schedule(worker);
            scheduler.Start();
            await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var unexpectedRetry = await Task.WhenAny(
                secondAttempt.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            await scheduler.Stop(CancellationToken.None);

            Assert.NotSame(secondAttempt.Task, unexpectedRetry);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Equal(1, scheduler.Diagnostics.TrackedFinalWorkerCount);
        }
    }

    [Fact]
    public async Task RecordPurgeFailuresAndClearTheAlertAfterALaterSuccessfulRun()
    {
        var index = new WorkerIndex();
        var attempt = 0;
        using var scheduler = new WorkerRetentionScheduler(
            index,
            WorkSystemRetentionConfiguration.Default,
            (workerIds, _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    throw new InvalidOperationException("retention store unavailable");
                }

                return workerIds.Count;
            });
        var first = CreateWorker("retention.failure.first", purgeInterval: TimeSpan.FromTicks(1));
        index.Register(first);
        scheduler.Schedule(first);
        scheduler.Start();

        await TestEventually.Until(() => Task.FromResult(
            scheduler.Diagnostics.SchedulerFailureMessage == "retention store unavailable"));

        var second = CreateWorker("retention.failure.second", purgeInterval: TimeSpan.FromTicks(1));
        index.Register(second);
        scheduler.Schedule(second);
        await TestEventually.Until(() => Task.FromResult(
            scheduler.Diagnostics.TotalPurgedCount == 1 &&
            scheduler.Diagnostics.SchedulerFailureMessage is null));
        await scheduler.Stop(CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref attempt));
        Assert.Null(scheduler.Diagnostics.SchedulerFailureType);
    }

    private static WorkerRecord CreateWorker(
        string definitionName,
        bool final = true,
        TimeSpan? purgeInterval = null,
        DateTimeOffset? createdAt = null)
    {
        var configuration = WorkConfiguration.Default with
        {
            Retention = WorkRetentionConfiguration.Default with
            {
                PurgeInterval = purgeInterval ?? TimeSpan.FromHours(1),
                MaximumFinalWorkers = 10,
            },
        };
        var definition = WorkDefinition.Create(definitionName, configuration: configuration);
        var timestamp = createdAt ?? DateTimeOffset.UtcNow;
        var worker = new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.Empty,
            WorkerOptions.Default,
            configuration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: timestamp,
            updatedAt: timestamp);
        if (final)
        {
            var cancel = worker.RequestCancel(
                worker.Revision,
                WorkRequestContext.Create(WorkInvocationChannel.InProcess));
            Assert.True(cancel.IsAccepted);
        }

        return worker;
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

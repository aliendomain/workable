using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class FailedWorkerAutoCancelSchedulerShould
{
    [Fact]
    public async Task StartStopAndRestartIdempotently()
    {
        using var scheduler = new FailedWorkerAutoCancelScheduler(_ => 0);

        scheduler.Start();
        scheduler.Start();
        await scheduler.Stop(CancellationToken.None);
        scheduler.Start();
        await scheduler.Stop(CancellationToken.None);
    }

    [Fact]
    public void DiscardEveryStaleScheduleShapeAndReturnAValidDueBatch()
    {
        using var scheduler = new FailedWorkerAutoCancelScheduler(_ => 0);
        var queue = GetField<PriorityQueue<FailedWorkerAutoCancelSchedule, DateTimeOffset>>(
            scheduler,
            "scheduledAutoCancels");
        var current = GetField<Dictionary<WorkerId, FailedWorkerAutoCancelSchedule>>(
            scheduler,
            "scheduledAutoCancelsByWorkerId");
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        var missing = new FailedWorkerAutoCancelSchedule(WorkerId.New(), 1, due);
        var wrongSequence = new FailedWorkerAutoCancelSchedule(WorkerId.New(), 1, due);
        var wrongDueAt = new FailedWorkerAutoCancelSchedule(WorkerId.New(), 1, due);
        queue.Enqueue(missing, due);
        queue.Enqueue(wrongSequence, due);
        queue.Enqueue(wrongDueAt, due);
        current[wrongSequence.WorkerId] = wrongSequence with { StateSequence = 2 };
        current[wrongDueAt.WorkerId] = wrongDueAt with { DueAt = due.AddMinutes(1) };

        Assert.False(TryTakeDueBatch(scheduler, out var staleBatch));
        Assert.Null(staleBatch);

        var valid = new FailedWorkerAutoCancelSchedule(WorkerId.New(), 3, due);
        queue.Enqueue(valid, due);
        current[valid.WorkerId] = valid;
        Assert.True(TryTakeDueBatch(scheduler, out var validBatch));
        Assert.Equal(valid, Assert.Single(validBatch!));
    }

    [Fact]
    public async Task ContainAutoCancelCallbackFailuresAndContinueScheduling()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new FailedWorkerAutoCancelScheduler(
            _ =>
            {
                attempted.TrySetResult();
                throw new InvalidOperationException("callback failed");
            },
            NullLogger.Instance);
        scheduler.Schedule(CreateDueFailedWorker());

        scheduler.Start();
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.Stop(CancellationToken.None);
    }

    private static WorkerRecord CreateDueFailedWorker()
    {
        var configuration = WorkConfiguration.Default with
        {
            FailedWorker = new WorkFailedWorkerConfiguration
            {
                Handling = WorkFailedWorkerHandling.AutoCancel,
                AutoCancelAfter = TimeSpan.FromTicks(1),
            },
        };
        var definition = WorkDefinition.Create("auto-cancel.scheduler", configuration: configuration);
        var now = DateTimeOffset.UtcNow;
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
            createdAt: now,
            updatedAt: now);
        Assert.True(worker.Start(worker.Revision, advancesRevision: false, out _, CancellationToken.None).IsAccepted);
        Assert.Equal(
            WorkCompletionStatus.Failed,
            worker.Complete(WorkExecutionResult.Failure([WorkMessage.Error("failed", "failed")])));
        return worker;
    }

    private static bool TryTakeDueBatch(
        FailedWorkerAutoCancelScheduler scheduler,
        out IReadOnlyList<FailedWorkerAutoCancelSchedule>? schedules)
    {
        object?[] arguments = [null];
        var result = (bool)typeof(FailedWorkerAutoCancelScheduler)
            .GetMethod("TryTakeDueBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, arguments)!;
        schedules = (IReadOnlyList<FailedWorkerAutoCancelSchedule>?)arguments[0];
        return result;
    }

    private static T GetField<T>(object target, string name)
        => (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}

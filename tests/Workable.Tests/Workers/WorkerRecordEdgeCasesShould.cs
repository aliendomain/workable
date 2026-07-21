using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerRecordEdgeCasesShould
{
    [Fact]
    public void RejectCompletionSignalsThatDoNotMatchTheCurrentLifecycle()
    {
        var queued = CreateWorker("record.invalid-completion");

        Assert.Equal(WorkCompletionStatus.Invalid, queued.Complete(WorkExecutionResult.Success()));
        Assert.Equal(WorkCompletionStatus.Invalid, queued.CompleteCancellation());
        Assert.Equal(WorkCompletionStatus.Invalid, queued.CompleteInterruption());
        Assert.Equal(
            WorkCompletionStatus.Invalid,
            queued.CompleteRecurringIteration(WorkExecutionResult.Success(), continueRecurrence: true));
        Assert.Equal(
            WorkCompletionStatus.Invalid,
            queued.CompleteRetryIteration(WorkExecutionResult.Success(), TimeSpan.Zero, retryAttempt: 1));
        Assert.Equal(WorkCompletionStatus.Invalid, queued.CompleteStoppedRecurrence());

        var interrupted = CreateWorker("record.completed-interruption");
        Assert.NotNull(interrupted.ForceInterrupt(WorkInterruptionReason.Shutdown));
        Assert.Equal(WorkCompletionStatus.Invalid, interrupted.CompleteCancellation());
        Assert.Equal(WorkCompletionStatus.Invalid, interrupted.CompleteInterruption());
        Assert.Null(interrupted.ForceInterrupt(WorkInterruptionReason.Shutdown));
    }

    [Fact]
    public void RejectReconfigurationAfterTheWorkerBecomesFinal()
    {
        var worker = CreateWorker("record.final-reconfigure");
        var cancel = worker.RequestCancel(
            worker.Revision,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkerState.Canceled, worker.State);

        var outcome = worker.Reconfigure(
            new WorkerReconfiguration(ProfilingEnabled: true),
            worker.Revision);

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message => message.Code == "workable.worker.final");
    }

    [Fact]
    public void FailedWorkerAutoCancelRequiresTheSameDueFailedState()
    {
        var configuration = WorkConfiguration.Default with
        {
            FailedWorker = new WorkFailedWorkerConfiguration
            {
                Handling = WorkFailedWorkerHandling.AutoCancel,
                AutoCancelAfter = TimeSpan.FromMinutes(1),
            },
        };
        var worker = CreateWorker("record.auto-cancel", configuration);

        Assert.Null(worker.GetFailedWorkerAutoCancelSchedule());
        Start(worker);
        Assert.Equal(
            WorkCompletionStatus.Failed,
            worker.Complete(WorkExecutionResult.Failure([WorkMessage.Error("test.failed", "Failed.")])));
        var schedule = Assert.IsType<FailedWorkerAutoCancelSchedule>(worker.GetFailedWorkerAutoCancelSchedule());

        Assert.False(worker.TryAutoCancelFailedWorker(
            schedule.StateSequence + 1,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            out var staleOutcome));
        Assert.Null(staleOutcome);
        Assert.False(worker.TryAutoCancelFailedWorker(
            schedule.StateSequence,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            out var earlyOutcome));
        Assert.Null(earlyOutcome);

        worker.SetFailedWorkerAutoCancelOverride(FailedWorkerAutoCancelOverride.Manual);
        Assert.Null(worker.GetFailedWorkerAutoCancelSchedule());
        worker.SetFailedWorkerAutoCancelOverride(FailedWorkerAutoCancelOverride.Explicit(TimeSpan.Zero));

        Assert.True(worker.TryAutoCancelFailedWorker(
            worker.StateSequence,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            out var accepted));
        Assert.NotNull(accepted);
        Assert.Equal(WorkerState.Canceled, worker.State);
    }

    [Fact]
    public void DistinguishLeaseLossFromShutdownInterruptionAndRespectPausedWorkers()
    {
        var shutdown = CreateWorker("record.shutdown-interruption");
        Assert.True(shutdown.RequestInterrupt(WorkInterruptionReason.Shutdown));
        Assert.Equal(WorkerState.Interrupted, shutdown.State);
        Assert.Contains(shutdown.ToSnapshot().Messages, message => message.Code == "workable.worker.shutdown_interrupted");

        var paused = CreateWorker("record.paused-lease-loss");
        Assert.True(paused.RequestPause(paused.Revision).IsAccepted);
        Assert.False(paused.RequestInterrupt(WorkInterruptionReason.Shutdown));
        Assert.True(paused.RequestInterrupt(WorkInterruptionReason.LeaseLost));
        Assert.Equal(WorkerState.Interrupted, paused.State);
        Assert.Contains(paused.ToSnapshot().Messages, message => message.Code == "workable.worker.lease_lost_interrupted");

        var forced = CreateWorker("record.forced-lease-loss");
        var snapshot = Assert.IsType<WorkerSnapshot>(forced.ForceInterrupt(WorkInterruptionReason.LeaseLost));
        Assert.Equal(WorkInterruptionReason.LeaseLost, snapshot.InterruptionReason);
        Assert.Contains(snapshot.Messages, message => message.Code == "workable.worker.lease_lost_interrupted_forced");
    }

    [Fact]
    public void CaptureLogsOnlyDuringIterationsAndKeepTraceSummariesCorrectWhenTheBufferTrims()
    {
        var disabled = CreateWorker(
            "record.logs.disabled",
            WorkConfiguration.Default with
            {
                Logging = WorkLoggingConfiguration.Default with { IsEnabled = false },
            });
        disabled.RecordLog(Log(disabled, LogLevel.Critical, "disabled"));
        Assert.Empty(disabled.ToSnapshot().Iterations);

        var worker = CreateWorker(
            "record.logs.trace",
            WorkConfiguration.Default with
            {
                Logging = WorkLoggingConfiguration.Default with
                {
                    Level = LogLevel.Trace,
                    MaximumBufferedEntries = 1,
                },
            });
        worker.RecordLog(Log(worker, LogLevel.Trace, "before-start"));
        Start(worker);

        var first = worker.RecordLog(Log(worker, LogLevel.Trace, "first"));
        var second = worker.RecordLog(Log(worker, LogLevel.Trace, "second"));
        worker.Complete(WorkExecutionResult.Success());
        var iteration = Assert.Single(worker.ToSnapshot().Iterations);

        Assert.Equal(0, first.Ordinal);
        Assert.Equal(1, second.Ordinal);
        Assert.Equal("second", Assert.Single(iteration.Logs).Message);
    }

    [Fact]
    public void RemovePerIterationActionHistoryWithoutCorruptingTimelineAccounting()
    {
        var configuration = WorkConfiguration.Default with
        {
            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)) with
            {
                RetainedIterations = 1,
            },
        };
        var worker = CreateWorker("record.timeline-retention", configuration);
        Start(worker);
        var pause = worker.RequestPause(worker.Revision);
        Assert.Equal(WorkCompletionStatus.Paused, worker.CompleteCancellation());

        worker.RecordActionHistory(
            pause,
            WorkRequestContext.Create(
                WorkInvocationChannel.HttpApi,
                new WorkActor("operator", "Operator", null),
                isAuthenticated: true));
        worker.RecordActionHistory(
            pause,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        Assert.Equal(2, worker.ToSnapshot().ActionHistory.Count);

        Start(worker);
        worker.CompleteRecurringIteration(WorkExecutionResult.Success(), continueRecurrence: true);
        var snapshot = worker.ToSnapshot();

        Assert.Empty(snapshot.ActionHistory);
        Assert.Single(snapshot.Iterations);
        Assert.Equal(2, snapshot.Iterations[0].Sequence);
    }

    [Fact]
    public void ReportNoConcurrencyCapacityWhenCoordinationIsDisabled()
    {
        var worker = CreateWorker("record.no-concurrency");

        Assert.False(worker.CountsAgainstConcurrencyCapacity(WorkConcurrencyBlockingMode.WhileExecuting));
        Assert.False(worker.TryGetConcurrencyCapacityContribution(out _, out var bucket));
        Assert.Null(bucket);
    }

    private static WorkerRecord CreateWorker(
        string definitionName,
        WorkConfiguration? configuration = null)
    {
        var resolvedConfiguration = configuration ?? WorkConfiguration.Default;
        var definition = WorkDefinition.Create(definitionName, configuration: resolvedConfiguration);
        var now = DateTimeOffset.UtcNow;
        return new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.Empty,
            WorkerOptions.Default,
            resolvedConfiguration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: now,
            updatedAt: now);
    }

    private static void Start(WorkerRecord worker)
    {
        var outcome = worker.Start(
            worker.Revision,
            advancesRevision: false,
            out _,
            CancellationToken.None);
        Assert.True(outcome.IsAccepted);
    }

    private static WorkerLogEntry Log(WorkerRecord worker, LogLevel level, string message)
        => new(
            DateTimeOffset.UtcNow,
            worker.Id,
            worker.Work.Definition.Id,
            "WorkerRecordEdgeCases",
            level,
            new EventId(1),
            message);

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}

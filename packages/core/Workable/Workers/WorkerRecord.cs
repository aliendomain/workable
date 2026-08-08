using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkerRecord(
    WorkerId id,
    RegisteredWork work,
    WorkInput? input,
    WorkerOptions options,
    WorkConfiguration configuration,
    WorkRequestContext requestContext,
    WorkerState state,
    bool isStartDeferred,
    IReadOnlyList<WorkMessage> messages,
    DateTimeOffset createdAt,
    DateTimeOffset updatedAt)
{
    private readonly Lock sync = new();
    private CancellationTokenSource? executionCancellation;
    private TaskCompletionSource<WorkerSnapshot> started = CreateSnapshotSource();
    private TaskCompletionSource<WorkCompletion> completion = CreateCompletionSource();
    private TaskCompletionSource recurrenceWaitSignal = CreateSignalSource();
    private readonly List<WorkerIterationSnapshot> retainedIterations = [];
    private readonly List<WorkerActionHistoryEntry> actionHistory = [];
    private readonly Dictionary<(WorkerState State, long StateSequence), int> suppressedTimelineStateRowCounts = [];
    private readonly HashSet<WorkIdentifier> identifiers = input?.Identifiers?.ToHashSet() ?? [];
    private readonly HashSet<WorkInitializationId> oncePerWorkerInitializersRun = [];
    private WorkProfileSnapshot? profile;
    private WorkProfileSnapshot? pendingIterationProfile;
    private CurrentWorkerIteration? currentIteration;
    private WorkRequestContext? cancellationRequestContext;
    private WorkInterruptionReason? interruptionReason;
    private DateTimeOffset? firstStartedAt;
    private DateTimeOffset? nextRunAt;
    private int? retryAttempt;
    private FailedWorkerAutoCancelOverride failedWorkerAutoCancelOverride = FailedWorkerAutoCancelOverride.None;
    private TimeSpan totalExecutionDuration;
    private long? lastIterationSequence;
    private long iterationSequence;
    private int logSummaryTotalCount;
    private int logSummaryCriticalCount;
    private int logSummaryErrorCount;
    private int logSummaryWarningCount;
    private int logSummaryInformationCount;
    private int logSummaryDebugCount;
    private int logSummaryTraceCount;
    private int timelineActionTotalCount;
    private int timelineActionUserCount;
    private int timelineActionSystemCount;
    private int timelineSyntheticStateRowCandidateCount;
    private int timelineIterationSystemCount;
    private int timelineIterationFailureCount;

    public WorkerId Id { get; } = id;

    public long Revision { get; private set; }

    public long StateSequence { get; private set; }

    public RegisteredWork Work { get; } = work;

    public WorkInput? Input { get; } = input;

    public WorkSubjectId? SubjectId => this.Input?.SubjectId;

    public WorkConcurrencyKey? ConcurrencyKey => this.Input?.ConcurrencyKey;

    public WorkRequestContext RequestContext { get; } = requestContext;

    public WorkOrigin Origin => this.RequestContext.Origin;

    public IReadOnlySet<WorkIdentifier> Identifiers
    {
        get
        {
            lock (this.sync)
            {
                return this.identifiers.ToHashSet();
            }
        }
    }

    public WorkerOptions Options { get; private set; } = options;

    public WorkConfiguration Configuration { get; private set; } = configuration;

    public WorkerState State { get; private set; } = state;

    public bool IsStartDeferred { get; private set; } = isStartDeferred;

    public IReadOnlyList<WorkMessage> Messages { get; private set; } = messages;

    public WorkOutput? Output { get; private set; }

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public DateTimeOffset StateChangedAt { get; private set; } = updatedAt;

    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;

    public Action<WorkerReadModelIterationUpdate>? IterationRecorded { get; set; }

    public Action<WorkerIterationReference>? IterationForgotten { get; set; }

    public bool IsFinal => WorkerStateMachine.IsFinal(this.State);

    public bool IsInterrupted
    {
        get
        {
            lock (this.sync)
            {
                return this.State is WorkerState.Interrupting or WorkerState.Interrupted;
            }
        }
    }

    public WorkInterruptionReason? InterruptionReason
    {
        get
        {
            lock (this.sync)
            {
                return this.interruptionReason;
            }
        }
    }

    public WorkRequestContext? CancellationRequestContext
    {
        get
        {
            lock (this.sync)
            {
                return this.cancellationRequestContext;
            }
        }
    }

    public bool IsCompletionSignaled
    {
        get
        {
            lock (this.sync)
            {
                return this.completion.Task.IsCompleted;
            }
        }
    }

    public WorkerSummary ToSummary()
    {
        lock (this.sync)
        {
            return this.ToSummaryLocked();
        }
    }

    public WorkerOverviewItem ToOverviewItem()
    {
        lock (this.sync)
        {
            return this.ToOverviewItemLocked();
        }
    }

    public bool AddIdentifier(WorkIdentifier identifier)
    {
        WorkerReadModelIterationUpdate? currentIteration = null;
        Action<WorkerReadModelIterationUpdate>? iterationRecorded = null;
        lock (this.sync)
        {
            if (!this.identifiers.Add(identifier))
            {
                return false;
            }

            this.MarkUpdated();
            if (this.currentIteration is not null)
            {
                currentIteration = this.CreateReadModelIterationUpdateLocked(
                    this.CreateCurrentIterationSnapshotLocked(DateTimeOffset.UtcNow));
                iterationRecorded = this.IterationRecorded;
            }
        }

        if (currentIteration is not null)
        {
            iterationRecorded?.Invoke(currentIteration);
        }

        return true;
    }

    public bool HasRunOncePerWorkerInitializer(WorkInitializationId initializationId)
    {
        lock (this.sync)
        {
            return this.oncePerWorkerInitializersRun.Contains(initializationId);
        }
    }

    public void MarkOncePerWorkerInitializerRun(WorkInitializationId initializationId)
    {
        lock (this.sync)
        {
            if (this.oncePerWorkerInitializersRun.Add(initializationId))
            {
                this.MarkUpdated();
            }
        }
    }

    public WorkActionOutcome Start(long expectedRevision, bool advancesRevision, out CancellationToken executionToken, CancellationToken cancellationToken)
    {
        WorkerStateTransition transition;
        lock (this.sync)
        {
            executionToken = default;
            var checkedTransition = this.CheckTransitionLocked(WorkAction.Start, expectedRevision);
            if (checkedTransition.Rejection is { } rejection)
            {
                return rejection;
            }

            transition = checkedTransition.RequiredTransition;

            var wasFailed = this.State == WorkerState.Failed;
            this.ReleaseExecutionCancellationLocked();
            this.executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.cancellationRequestContext = null;
            executionToken = this.executionCancellation.Token;
            this.ApplyAcceptedTransitionLocked(transition, advancesRevision);
            this.IsStartDeferred = false;
            this.Output = null;
            this.retryAttempt = null;
            this.failedWorkerAutoCancelOverride = FailedWorkerAutoCancelOverride.None;
            this.BeginIterationLocked();
            if (this.started.Task.IsCompleted)
            {
                this.started = CreateSnapshotSource();
            }

            if (this.completion.Task.IsCompleted)
            {
                this.completion = CreateCompletionSource();
            }

            if (wasFailed)
            {
                this.Messages = [];
            }

            var outcome = this.ToOutcomeLocked(transition);
            this.started.TrySetResult(this.ToSnapshotLocked());
            return outcome;
        }
    }

    public void TrackCompletion(Task<WorkCompletion> task)
    {
        TaskCompletionSource<WorkCompletion> completionToSignal;
        lock (this.sync)
        {
            completionToSignal = this.completion;
        }

        CompleteWhenExecutionFinishes(task, completionToSignal);
    }

    public WorkActionOutcome RequestPause(long expectedRevision)
    {
        CancellationTokenSource? cancellation = null;
        WorkActionOutcome outcome;

        lock (this.sync)
        {
            var checkedTransition = this.CheckTransitionLocked(WorkAction.Pause, expectedRevision);
            if (checkedTransition.Rejection is { } rejection)
            {
                return rejection;
            }

            var transition = checkedTransition.RequiredTransition;
            this.ApplyAcceptedTransitionLocked(transition);

            cancellation = transition.CancelsExecution ? this.executionCancellation : null;
            this.SignalRecurrenceWaitLocked();
            outcome = this.ToOutcomeLocked(transition);
        }

        CancelIfAvailable(cancellation);

        return outcome;
    }

    public WorkActionOutcome RequestCancel(
        long expectedRevision,
        WorkRequestContext cancellationRequestContext)
    {
        ArgumentNullException.ThrowIfNull(cancellationRequestContext);

        CancellationTokenSource? cancellation = null;
        WorkActionOutcome outcome;

        lock (this.sync)
        {
            var checkedTransition = this.CheckTransitionLocked(WorkAction.Cancel, expectedRevision);
            if (checkedTransition.Rejection is { } rejection)
            {
                return rejection;
            }

            var transition = checkedTransition.RequiredTransition;
            this.cancellationRequestContext = cancellationRequestContext;
            this.ApplyAcceptedTransitionLocked(transition);

            cancellation = transition.CancelsExecution ? this.executionCancellation : null;
            this.SignalRecurrenceWaitLocked();
            outcome = this.ToOutcomeLocked(transition);
        }

        CancelIfAvailable(cancellation);

        return outcome;
    }

    public WorkActionOutcome Push(long expectedRevision)
    {
        lock (this.sync)
        {
            var checkedTransition = this.CheckTransitionLocked(WorkAction.Push, expectedRevision);
            if (checkedTransition.Rejection is { } rejection)
            {
                return rejection;
            }

            var transition = checkedTransition.RequiredTransition;
            this.ApplyAcceptedTransitionLocked(transition);
            this.SignalRecurrenceWaitLocked();

            return this.ToOutcomeLocked(
                transition,
                [WorkMessage.Info("workable.worker.pushed", "Worker push was accepted.")]);
        }
    }

    public WorkActionOutcome Purge(long expectedRevision)
    {
        lock (this.sync)
        {
            var checkedTransition = this.CheckTransitionLocked(WorkAction.Purge, expectedRevision);
            if (checkedTransition.Rejection is { } rejection)
            {
                return rejection;
            }

            var transition = checkedTransition.RequiredTransition;
            this.ApplyAcceptedTransitionLocked(transition, changesState: false);

            return this.ToOutcomeLocked(transition);
        }
    }

    public WorkCompletionStatus Complete(WorkExecutionResult result)
    {
        lock (this.sync)
        {
            return this.CompleteLocked(result);
        }
    }

    public WorkCompletionStatus CompleteCancellation()
    {
        lock (this.sync)
        {
            if (this.completion.Task.IsCompleted)
            {
                return WorkCompletionStatus.Invalid;
            }

            var transition = WorkerStateMachine.CompleteCancellation(this.State);
            if (transition.CompletionStatus == WorkCompletionStatus.Invalid)
            {
                return transition.CompletionStatus;
            }

            this.SetStateLocked(transition.NextState);
            this.Output = null;
            this.RecordIterationLocked(null, this.Messages, transition.CompletionStatus);
            this.nextRunAt = null;
            this.retryAttempt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
            return transition.CompletionStatus;
        }
    }

    public WorkCompletionStatus CompleteInterruption()
    {
        lock (this.sync)
        {
            if (this.completion.Task.IsCompleted)
            {
                return WorkCompletionStatus.Invalid;
            }

            var transition = WorkerStateMachine.CompleteInterruption(this.State);
            if (transition.CompletionStatus == WorkCompletionStatus.Invalid)
            {
                return transition.CompletionStatus;
            }

            this.SetStateLocked(transition.NextState);
            this.Output = null;
            this.RecordIterationLocked(null, this.Messages, transition.CompletionStatus);
            this.nextRunAt = null;
            this.retryAttempt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
            return transition.CompletionStatus;
        }
    }

    public WorkCompletionStatus CompleteRecurringIteration(WorkExecutionResult result, bool continueRecurrence)
    {
        lock (this.sync)
        {
            if (this.State is WorkerState.Pausing or WorkerState.Canceling || !continueRecurrence)
            {
                return this.CompleteLocked(result);
            }

            if (this.State != WorkerState.Running)
            {
                return WorkCompletionStatus.Invalid;
            }

            this.SetStateLocked(WorkerState.Waiting);
            this.Output = result.Output;
            this.Messages = result.Messages;
            this.recurrenceWaitSignal = CreateSignalSource();
            this.nextRunAt = DateTimeOffset.UtcNow + this.Configuration.Recurrence.Interval;
            this.retryAttempt = null;
            this.RecordIterationLocked(result, result.HasErrors ? WorkCompletionStatus.Failed : WorkCompletionStatus.Completed);
            this.AdvanceStateSequence();
            return WorkCompletionStatus.Invalid;
        }
    }

    public WorkCompletionStatus CompleteRetryIteration(WorkExecutionResult result, TimeSpan retryDelay, int retryAttempt)
    {
        lock (this.sync)
        {
            if (this.State is WorkerState.Pausing or WorkerState.Canceling)
            {
                return this.CompleteLocked(result);
            }

            if (this.State != WorkerState.Running)
            {
                return WorkCompletionStatus.Invalid;
            }

            this.SetStateLocked(WorkerState.Retrying);
            this.Output = result.Output;
            this.Messages = result.Messages;
            this.recurrenceWaitSignal = CreateSignalSource();
            this.nextRunAt = DateTimeOffset.UtcNow + retryDelay;
            this.retryAttempt = retryAttempt;
            this.RecordIterationLocked(result, WorkCompletionStatus.Failed);
            this.AdvanceStateSequence();
            return WorkCompletionStatus.Invalid;
        }
    }

    public WorkCompletionStatus CompleteStoppedRecurrence()
    {
        lock (this.sync)
        {
            if (this.State != WorkerState.Waiting)
            {
                return WorkerStateMachine.CompletionStatusFor(this.State);
            }

            var status = this.Messages.Any(message => message.Severity.IsError())
                ? WorkCompletionStatus.Failed
                : WorkCompletionStatus.Completed;
            this.SetStateLocked(status == WorkCompletionStatus.Failed
                ? WorkerState.Failed
                : WorkerState.Completed);
            this.nextRunAt = null;
            this.retryAttempt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
            return status;
        }
    }

    public async Task WaitForRecurrenceInterval(TimeSpan interval, CancellationToken cancellationToken)
    {
        Task signal;
        lock (this.sync)
        {
            signal = this.recurrenceWaitSignal.Task;
        }

        var delay = Task.Delay(interval, cancellationToken);
        await await Task.WhenAny(signal, delay);
    }

    public bool TryBeginNextRecurringIteration()
    {
        lock (this.sync)
        {
            if (this.State is not (WorkerState.Waiting or WorkerState.Queued))
            {
                return false;
            }

            this.SetStateLocked(WorkerState.Running);
            this.Output = null;
            this.Messages = [];
            this.nextRunAt = null;
            this.retryAttempt = null;
            this.BeginIterationLocked();
            this.AdvanceStateSequence();
            return true;
        }
    }

    public bool TryBeginRetryIteration()
    {
        lock (this.sync)
        {
            if (this.State is not (WorkerState.Retrying or WorkerState.Queued))
            {
                return false;
            }

            this.SetStateLocked(WorkerState.Running);
            this.Output = null;
            this.Messages = [];
            this.nextRunAt = null;
            this.BeginIterationLocked();
            this.retryAttempt = null;
            this.AdvanceStateSequence();
            return true;
        }
    }

    public void Fail(WorkMessage message)
    {
        lock (this.sync)
        {
            this.Messages = [message];
            this.SetStateLocked(WorkerState.Failed);
            this.RecordIterationLocked(null, this.Messages, WorkCompletionStatus.Failed);
            this.nextRunAt = null;
            this.retryAttempt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
        }
    }

    public WorkActionOutcome Reconfigure(
        WorkerReconfiguration changes,
        long expectedRevision,
        bool persistenceStoreAvailable = true)
    {
        lock (this.sync)
        {
            if (this.CheckRevision(WorkAction.Start, expectedRevision) is { } conflict)
            {
                return conflict;
            }

            if (this.IsFinal)
            {
                return WorkActionOutcome.Invalid(
                    WorkAction.Start,
                    this.ToSnapshotLocked(),
                    [WorkMessage.Error("workable.worker.final", "Final workers cannot be reconfigured.", "worker")]);
            }

            var options = this.Options with
            {
                ProfilingEnabled = changes.ProfilingEnabled ?? this.Options.ProfilingEnabled,
            };

            var configuration = this.Configuration;
            if (changes.Recurrence is not null)
            {
                configuration = configuration with
                {
                    Recurrence = changes.Recurrence,
                };
            }

            if (changes.TransientRetry is not null)
            {
                configuration = configuration with
                {
                    TransientRetry = changes.TransientRetry,
                };
            }

            if (changes.FailedWorker is not null)
            {
                configuration = configuration with
                {
                    FailedWorker = changes.FailedWorker,
                };
            }

            if (changes.Logging is not null)
            {
                configuration = configuration with
                {
                    Logging = changes.Logging,
                };
            }

            if (changes.Retention is not null)
            {
                configuration = configuration with
                {
                    Retention = changes.Retention,
                };
            }

            if (changes.Coordination is not null)
            {
                configuration = configuration with
                {
                    Coordination = changes.Coordination,
                };
            }

            if (changes.Start is not null)
            {
                configuration = configuration with
                {
                    Start = changes.Start,
                };
            }

            var configurationErrors = WorkConfigurationValidator.Validate(configuration)
                .Concat(WorkConfigurationValidator.ValidatePersistenceStore(configuration, persistenceStoreAvailable))
                .ToList();
            if (configurationErrors.Count > 0)
            {
                return WorkActionOutcome.Invalid(WorkAction.Start, this.ToSnapshotLocked(), configurationErrors);
            }

            var concurrencyInputErrors = WorkConfigurationValidator.ValidateConcurrencyInput(
                coordination: configuration.Coordination,
                input: this.Input);
            if (concurrencyInputErrors.Count > 0)
            {
                return WorkActionOutcome.Invalid(WorkAction.Start, this.ToSnapshotLocked(), concurrencyInputErrors);
            }

            this.Options = options;
            this.Configuration = configuration;
            if (!this.Configuration.Coordination.IsConcurrencyEnabled)
            {
                this.IsStartDeferred = false;
            }

            this.AdvanceRevision();
            if (changes.Recurrence is not null && !changes.Recurrence.IsEnabled)
            {
                this.SignalRecurrenceWaitLocked();
            }

            return WorkActionOutcome.Accepted(
                WorkAction.Start,
                this.ToSnapshotLocked(),
                [WorkMessage.Info("workable.worker.reconfigured", "Worker configuration was updated.")]);
        }
    }

    public void SetFailedWorkerAutoCancelOverride(FailedWorkerAutoCancelOverride failedWorkerAutoCancelOverride)
    {
        lock (this.sync)
        {
            this.failedWorkerAutoCancelOverride = failedWorkerAutoCancelOverride;
        }
    }

    public FailedWorkerAutoCancelSchedule? GetFailedWorkerAutoCancelSchedule()
    {
        lock (this.sync)
        {
            if (this.State != WorkerState.Failed)
            {
                return null;
            }

            var autoCancelAfter = this.ResolveFailedWorkerAutoCancelDelayLocked();
            if (autoCancelAfter is null)
            {
                return null;
            }

            return new FailedWorkerAutoCancelSchedule(
                this.Id,
                this.StateSequence,
                this.StateChangedAt + autoCancelAfter.Value);
        }
    }

    public bool TryAutoCancelFailedWorker(
        long requiredStateSequence,
        WorkRequestContext cancellationRequestContext,
        out WorkActionOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(cancellationRequestContext);

        lock (this.sync)
        {
            outcome = null;
            if (this.State != WorkerState.Failed || this.StateSequence != requiredStateSequence)
            {
                return false;
            }

            var autoCancelAfter = this.ResolveFailedWorkerAutoCancelDelayLocked();
            if (autoCancelAfter is null || this.StateChangedAt + autoCancelAfter.Value > DateTimeOffset.UtcNow)
            {
                return false;
            }

            var checkedTransition = this.CheckTransitionLocked(WorkAction.Cancel, this.Revision);
            if (checkedTransition.Rejection is not null)
            {
                return false;
            }

            var transition = checkedTransition.RequiredTransition;
            this.cancellationRequestContext = cancellationRequestContext;
            this.ApplyAcceptedTransitionLocked(transition);
            this.SignalRecurrenceWaitLocked();
            outcome = this.ToOutcomeLocked(transition);
            return true;
        }
    }

    public async Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken)
    {
        Task<WorkCompletion> task;
        lock (this.sync)
        {
            task = this.completion.Task;
        }

        return await task.WaitAsync(cancellationToken);
    }

    public async Task WaitForStarted(CancellationToken cancellationToken)
    {
        Task<WorkerSnapshot> startedTask;
        Task<WorkCompletion> completionTask;
        lock (this.sync)
        {
            startedTask = this.started.Task;
            completionTask = this.completion.Task;
        }

        await (await Task.WhenAny(startedTask, completionTask)).WaitAsync(cancellationToken);
    }

    public bool SignalCurrentCompletion()
    {
        lock (this.sync)
        {
            if (this.completion.Task.IsCompleted)
            {
                return false;
            }

            var status = WorkerStateMachine.CompletionStatusFor(this.State);
            if (status == WorkCompletionStatus.Invalid)
            {
                return false;
            }

            this.SetCompletionLocked(status);
            return true;
        }
    }

    public WorkerSnapshot ToSnapshot()
    {
        lock (this.sync)
        {
            return this.ToSnapshotLocked();
        }
    }

    public WorkerSnapshot? ForceInterrupt(WorkInterruptionReason reason)
    {
        lock (this.sync)
        {
            if (this.completion.Task.IsCompleted)
            {
                return null;
            }

            this.Messages =
            [
                .. this.Messages,
                CreateInterruptionMessage(reason, forced: true),
            ];
            this.interruptionReason = reason;
            this.SetStateLocked(WorkerState.Interrupted);
            this.Output = null;
            this.RecordIterationLocked(null, this.Messages, WorkCompletionStatus.Interrupted);
            this.nextRunAt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
            this.SignalRecurrenceWaitLocked();
            this.SetCompletionLocked(WorkCompletionStatus.Interrupted);

            return this.ToSnapshotLocked();
        }
    }

    public WorkerSnapshot? ForceInterruptForSystemStop()
        => this.ForceInterrupt(WorkInterruptionReason.Shutdown);

    public WorkerReadModelWorker ToReadModelWorker()
    {
        lock (this.sync)
        {
            return this.CreateReadModelWorkerLocked();
        }
    }

    public WorkerIterationSnapshot? GetIterationSnapshot(long sequence)
    {
        lock (this.sync)
        {
            return this.GetIterationSnapshotLocked(sequence);
        }
    }

    public bool ShouldCaptureLog(LogLevel level)
    {
        lock (this.sync)
        {
            return level != LogLevel.None &&
                this.Configuration.Logging.IsEnabled &&
                level >= this.Configuration.Logging.Level;
        }
    }

    public WorkerLogEntry RecordLog(WorkerLogEntry entry)
    {
        lock (this.sync)
        {
            var logging = this.Configuration.Logging;
            if (!logging.IsEnabled || entry.Level < logging.Level || logging.MaximumBufferedEntries <= 0)
            {
                return entry;
            }

            if (this.currentIteration is not { } currentIteration)
            {
                return entry;
            }

            var storedEntry = entry with { Ordinal = currentIteration.NextLogOrdinal++ };
            currentIteration.Logs.Add(storedEntry);
            this.AddLogSummaryEntryLocked(storedEntry);
            while (currentIteration.Logs.Count > logging.MaximumBufferedEntries)
            {
                this.RemoveLogSummaryEntryLocked(currentIteration.Logs[0]);
                currentIteration.Logs.RemoveAt(0);
            }

            this.MarkUpdated();
            return storedEntry;
        }
    }

    public void RecordProfile(WorkProfileSnapshot profile)
    {
        lock (this.sync)
        {
            this.profile = profile;
            this.pendingIterationProfile = profile;
            this.MarkUpdated();
        }
    }

    public void RecordActionHistory(WorkActionOutcome outcome, WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(requestContext);

        lock (this.sync)
        {
            var entry = new WorkerActionHistoryEntry(
                DateTimeOffset.UtcNow,
                WorkerActionHistoryKind.WorkerAction,
                outcome.Action,
                outcome.Status,
                requestContext,
                this.Revision,
                this.StateSequence,
                this.State,
                outcome.Messages,
                this.GetLatestTrackedIterationSequenceLocked());
            this.actionHistory.Add(entry);
            this.AddTimelineActionEntryLocked(entry);
            this.MarkUpdated();
        }
    }

    public void RecordReconfigurationHistory(
        WorkerReconfiguration reconfiguration,
        WorkActionOutcome outcome,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(reconfiguration);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(requestContext);

        lock (this.sync)
        {
            var entry = new WorkerActionHistoryEntry(
                DateTimeOffset.UtcNow,
                WorkerActionHistoryKind.Reconfiguration,
                null,
                outcome.Status,
                requestContext,
                this.Revision,
                this.StateSequence,
                this.State,
                outcome.Messages,
                this.GetLatestTrackedIterationSequenceLocked(),
                reconfiguration);
            this.actionHistory.Add(entry);
            this.AddTimelineActionEntryLocked(entry);
            this.MarkUpdated();
        }
    }

    public bool CountsAgainstConcurrencyCapacity(WorkConcurrencyBlockingMode blockingMode)
    {
        lock (this.sync)
        {
            if (!TryGetConcurrencyCapacityBucketLocked(this.State, this.Configuration, this.IsStartDeferred, out var bucket) ||
                bucket is null)
            {
                return false;
            }

            return bucket.Value.CountsFor(blockingMode);
        }
    }

    internal bool TryGetConcurrencyCapacityContribution(
        out WorkConcurrencyScope scope,
        [NotNullWhen(true)] out WorkConcurrencyCapacityBucket? bucket)
    {
        lock (this.sync)
        {
            scope = this.Configuration.Coordination.Concurrency.Scope;
            return TryGetConcurrencyCapacityBucketLocked(this.State, this.Configuration, this.IsStartDeferred, out bucket);
        }
    }

    public bool IsDeferredConcurrencyStartFor(WorkDefinitionId definitionId)
    {
        lock (this.sync)
        {
            return this.Work.Definition.Id == definitionId &&
                this.Configuration.Coordination.IsConcurrencyEnabled &&
                this.ShouldStartAutomatically() &&
                this.State == WorkerState.Queued &&
                this.IsStartDeferred;
        }
    }

    public void ReserveDeferredConcurrencyStart()
    {
        lock (this.sync)
        {
            if (this.State == WorkerState.Queued)
            {
                this.IsStartDeferred = false;
            }
        }
    }

    public bool ShouldStartWithoutConcurrency()
    {
        lock (this.sync)
        {
            return this.State == WorkerState.Queued &&
                this.ShouldStartAutomatically() &&
                !this.Configuration.Coordination.IsConcurrencyEnabled;
        }
    }

    public bool ShouldStartWithConcurrency()
    {
        lock (this.sync)
        {
            return this.State == WorkerState.Queued &&
                this.ShouldStartAutomatically() &&
                this.Configuration.Coordination.IsConcurrencyEnabled &&
                !this.IsStartDeferred;
        }
    }

    public void DeferConcurrencyStart()
    {
        lock (this.sync)
        {
            if (this.State == WorkerState.Queued && this.Configuration.Coordination.IsConcurrencyEnabled)
            {
                this.IsStartDeferred = true;
            }
        }
    }

    private WorkerSnapshot ToSnapshotLocked()
    {
        var iterations = this.retainedIterations
            .OrderBy(iteration => iteration.Sequence)
            .ToArray();

        var currentIteration = this.currentIteration is null
            ? null
            : this.CreateCurrentIterationSnapshotLocked(DateTimeOffset.UtcNow);
        var latestIteration = this.GetLatestIterationLocked();
        return new(
            this.Id,
            this.Revision,
            this.StateSequence,
            this.Work.Definition.Name,
            this.Work.Definition.Category,
            this.SubjectId,
            this.ConcurrencyKey,
            this.identifiers.ToHashSet(),
            this.RequestContext,
            this.State,
            this.Input,
            this.Output,
            this.Options,
            this.Configuration,
            this.Messages,
            this.interruptionReason,
            this.CreatedAt,
            this.StateChangedAt,
            this.UpdatedAt)
        {
            Iterations = iterations,
            CurrentIteration = currentIteration,
            LastIteration = latestIteration,
            CurrentIterationSequence = this.currentIteration?.Sequence,
            LastIterationSequence = this.lastIterationSequence,
            ActionHistory = [.. this.actionHistory],
            Profile = this.profile,
            RetryAttempt = this.retryAttempt,
            QueueDuration = this.QueueDurationLocked(),
            TotalExecutionDuration = this.TotalExecutionDurationLocked(),
            NextRunAt = this.nextRunAt,
        };
    }

    private WorkerReadModelWorker CreateReadModelWorkerLocked()
        => WorkerReadModelWorker.From(
            this.Work.Definition.Id,
            this.ToOverviewItemLocked(),
            this.Configuration.Recurrence.IsEnabled,
            this.Configuration.Coordination.IsConcurrencyEnabled,
            this.Options.ProfilingEnabled,
            this.RequestContext.Actor.Id);

    private WorkerReadModelIterationUpdate CreateReadModelIterationUpdateLocked(WorkerIterationSnapshot iteration)
    {
        var worker = this.CreateReadModelWorkerLocked();
        return new WorkerReadModelIterationUpdate(
            worker,
            WorkerReadModelIteration.From(worker, iteration),
            iteration);
    }

    private static TaskCompletionSource<WorkCompletion> CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<WorkerSnapshot> CreateSnapshotSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CreateSignalSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void CompleteWhenExecutionFinishes(
        Task<WorkCompletion> execution,
        TaskCompletionSource<WorkCompletion> completion)
    {
        _ = execution.ContinueWith(
            static (completedExecution, state) =>
            {
                var completion = (TaskCompletionSource<WorkCompletion>)state!;
                if (completedExecution.IsCompletedSuccessfully)
                {
                    completion.TrySetResult(completedExecution.Result);
                    return;
                }

                if (completedExecution.IsCanceled)
                {
                    completion.TrySetCanceled();
                    return;
                }

                if (completedExecution.Exception is { } exception)
                {
                    completion.TrySetException(exception.InnerExceptions);
                    return;
                }

                completion.TrySetException(new InvalidOperationException("Worker execution finished without a completion result."));
            },
            completion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SetCompletionLocked(WorkCompletionStatus status)
        => this.completion.TrySetResult(this.ToCompletionLocked(status));

    private WorkCompletionStatus CompleteLocked(WorkExecutionResult result)
    {
        var transition = WorkerStateMachine.Complete(this.State, result.HasErrors);

        if (transition.CompletionStatus == WorkCompletionStatus.Invalid)
        {
            return transition.CompletionStatus;
        }

        this.SetStateLocked(transition.NextState);
        this.Output = transition.CompletionStatus is WorkCompletionStatus.Completed or WorkCompletionStatus.Failed ? result.Output : null;
        this.Messages = result.Messages;
        this.RecordIterationLocked(result, transition.CompletionStatus);
        this.nextRunAt = null;
        this.AdvanceStateSequence();
        this.ReleaseExecutionCancellationLocked();
        return transition.CompletionStatus;
    }

    private void RecordIterationLocked(WorkExecutionResult result, WorkCompletionStatus status)
        => this.RecordIterationLocked(result.Output, result.Messages, status);

    private void RecordIterationLocked(WorkOutput? output, IReadOnlyList<WorkMessage> messages, WorkCompletionStatus status)
    {
        if (this.currentIteration is not { } iterationInProgress)
        {
            return;
        }

        var completedAt = DateTimeOffset.UtcNow;
        var executionDuration = completedAt - iterationInProgress.StartedAt;
        this.totalExecutionDuration += executionDuration;
        var iteration = new WorkerIterationSnapshot(
            iterationInProgress.Sequence,
            iterationInProgress.StartedAt,
            completedAt,
            executionDuration,
            status,
            iterationInProgress.AttemptCount,
            output,
            messages)
        {
            Logs = [.. iterationInProgress.Logs],
            Profile = this.pendingIterationProfile,
        };
        this.pendingIterationProfile = null;
        this.currentIteration = null;
        this.lastIterationSequence = iteration.Sequence;
        this.RemoveTimelineIterationEntryLocked(WorkCompletionStatus.Executing);
        this.retainedIterations.Add(iteration);
        this.AddTimelineIterationEntryLocked(iteration.Status);

        var maximum = this.Configuration.Recurrence.RetainedIterations;
        var forgottenIterations = new List<WorkerIterationReference>();
        while (this.retainedIterations.Count > maximum)
        {
            var forgotten = this.retainedIterations[0];
            this.retainedIterations.RemoveAt(0);
            this.RemoveActionHistoryForIterationLocked(forgotten.Sequence);
            foreach (var entry in forgotten.Logs)
            {
                this.RemoveLogSummaryEntryLocked(entry);
            }

            this.RemoveTimelineIterationEntryLocked(forgotten.Status);
            forgottenIterations.Add(new WorkerIterationReference(this.Id, forgotten.Sequence));
        }

        this.IterationRecorded?.Invoke(this.CreateReadModelIterationUpdateLocked(iteration));
        foreach (var forgotten in forgottenIterations)
        {
            this.IterationForgotten?.Invoke(forgotten);
        }
    }

    private void BeginIterationLocked()
    {
        var startedAt = DateTimeOffset.UtcNow;
        this.currentIteration = new CurrentWorkerIteration(
            ++this.iterationSequence,
            startedAt,
            this.retryAttempt.HasValue ? this.retryAttempt.Value + 1 : 1);
        this.pendingIterationProfile = null;
        this.firstStartedAt ??= startedAt;
        this.AddTimelineIterationEntryLocked(WorkCompletionStatus.Executing);
        this.IterationRecorded?.Invoke(this.CreateReadModelIterationUpdateLocked(
            this.CreateCurrentIterationSnapshotLocked(startedAt)));
    }

    private WorkerIterationSnapshot CreateCurrentIterationSnapshotLocked(DateTimeOffset observedAt)
    {
        var iteration = this.currentIteration ?? throw new InvalidOperationException("Current iteration was not available.");
        return new WorkerIterationSnapshot(
            iteration.Sequence,
            iteration.StartedAt,
            observedAt,
            observedAt - iteration.StartedAt,
            WorkCompletionStatus.Executing,
            iteration.AttemptCount,
            Output: null,
            Messages: this.Messages)
        {
            Logs = [.. iteration.Logs],
            Profile = this.pendingIterationProfile,
        };
    }

    public WorkConfiguration GetConfiguration()
    {
        lock (this.sync)
        {
            return this.Configuration;
        }
    }

    public void DisposeExecutionResources(CancellationToken executionToken)
    {
        lock (this.sync)
        {
            if (this.executionCancellation?.Token != executionToken)
            {
                return;
            }

            this.ReleaseExecutionCancellationLocked();
        }
    }

    private CheckedWorkerTransition CheckTransitionLocked(WorkAction action, long expectedRevision)
    {
        if (this.CheckRevision(action, expectedRevision) is { } conflict)
        {
            return CheckedWorkerTransition.Rejected(conflict);
        }

        var transition = WorkerStateMachine.Apply(this.State, action);
        return transition.IsAccepted
            ? CheckedWorkerTransition.Accepted(transition)
            : CheckedWorkerTransition.Rejected(this.ToOutcomeLocked(transition));
    }

    private void ApplyAcceptedTransitionLocked(
        WorkerStateTransition transition,
        bool advancesRevision = true,
        bool changesState = true)
    {
        if (!transition.IsAccepted)
        {
            throw new InvalidOperationException("Only accepted worker transitions can be applied.");
        }

        if (changesState && !transition.RemovesWorker)
        {
            this.SetStateLocked(transition.RequiredNextState);
        }

        if (advancesRevision)
        {
            this.AdvanceRevision();
        }

        this.AdvanceStateSequence();
    }

    private WorkActionOutcome ToOutcomeLocked(WorkerStateTransition transition, IEnumerable<WorkMessage>? acceptedMessages = null)
        => transition.Status switch
        {
            WorkActionStatus.Accepted => WorkActionOutcome.Accepted(transition.Action, this.ToSnapshotLocked(), acceptedMessages),
            WorkActionStatus.Conflict => WorkActionOutcome.Conflict(transition.Action, this.ToSnapshotLocked(), [ToMessage(transition)]),
            _ => WorkActionOutcome.Invalid(transition.Action, this.ToSnapshotLocked(), [ToMessage(transition)]),
        };

    private static WorkMessage ToMessage(WorkerStateTransition transition)
    {
        if (transition.MessageCode is null || transition.MessageText is null)
        {
            throw new InvalidOperationException("Rejected worker transition did not include message details.");
        }

        return WorkMessage.Error(transition.MessageCode, transition.MessageText, "worker");
    }

    public bool RequestInterrupt(WorkInterruptionReason reason)
    {
        CancellationTokenSource? cancellation = null;
        lock (this.sync)
        {
            if (!CanInterrupt(this.State, reason))
            {
                return false;
            }

            var currentState = this.State;
            this.Messages =
            [
                .. this.Messages,
                CreateInterruptionMessage(reason, forced: false),
            ];
            this.interruptionReason = reason;
            this.SetStateLocked(currentState == WorkerState.Running
                ? WorkerState.Interrupting
                : WorkerState.Interrupted);
            this.AdvanceStateSequence();

            cancellation = currentState == WorkerState.Running ? this.executionCancellation : null;
            this.SignalRecurrenceWaitLocked();
        }

        CancelIfAvailable(cancellation);

        return true;
    }

    public bool RequestInterruptForSystemStop()
        => this.RequestInterrupt(WorkInterruptionReason.Shutdown);

    private static bool CanInterrupt(WorkerState state, WorkInterruptionReason reason)
        => reason == WorkInterruptionReason.LeaseLost
            ? state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Paused
            : state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying;

    private static WorkMessage CreateInterruptionMessage(WorkInterruptionReason reason, bool forced)
        => reason switch
        {
            WorkInterruptionReason.LeaseLost => WorkMessage.Warning(
                forced
                    ? "workable.worker.lease_lost_interrupted_forced"
                    : "workable.worker.lease_lost_interrupted",
                forced
                    ? "Worker was marked interrupted because this runtime lost its durable queue lease."
                    : "Worker execution was interrupted because this runtime lost its durable queue lease.",
                "worker"),
            _ => WorkMessage.Warning(
                forced
                    ? "workable.worker.shutdown_interrupted_forced"
                    : "workable.worker.shutdown_interrupted",
                forced
                    ? "Worker did not stop within the shutdown grace period and was marked interrupted by Workable."
                    : "Worker execution was interrupted because the Workable system is stopping.",
                "worker"),
        };

    private static bool CancelIfAvailable(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private WorkActionOutcome? CheckRevision(WorkAction action, long expectedRevision)
    {
        if (expectedRevision != this.Revision)
        {
            return WorkActionOutcome.Conflict(
                action,
                this.ToSnapshotLocked(),
                [WorkMessage.Error(
                    "workable.worker.revision_conflict",
                    $"Worker revision conflict. Expected revision '{expectedRevision}', but current revision is '{this.Revision}'.",
                    "worker.revision")]);
        }

        return null;
    }

    private void AdvanceRevision()
    {
        this.Revision++;
        this.MarkUpdated();
    }

    private void SetStateLocked(WorkerState state)
    {
        if (this.State == state)
        {
            return;
        }

        this.State = state;
        this.StateChangedAt = DateTimeOffset.UtcNow;
    }

    private void AdvanceStateSequence()
    {
        this.StateSequence++;
        this.MarkUpdated();
    }

    private void MarkUpdated()
        => this.UpdatedAt = DateTimeOffset.UtcNow;

    private bool ShouldStartAutomatically()
        => this.Configuration.Start.Policy != WorkStartPolicy.DoNotStart;

    private static bool TryGetConcurrencyCapacityBucketLocked(
        WorkerState state,
        WorkConfiguration configuration,
        bool isStartDeferred,
        [NotNullWhen(true)] out WorkConcurrencyCapacityBucket? bucket)
    {
        if (!configuration.Coordination.IsConcurrencyEnabled)
        {
            bucket = null;
            return false;
        }

        if (state == WorkerState.Queued)
        {
            if (configuration.Start.Policy == WorkStartPolicy.DoNotStart || isStartDeferred)
            {
                bucket = null;
                return false;
            }

            bucket = WorkConcurrencyCapacityBucket.Executing;
            return true;
        }

        bucket = state switch
        {
            WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Interrupting or WorkerState.Canceling => WorkConcurrencyCapacityBucket.Executing,
            WorkerState.Paused => WorkConcurrencyCapacityBucket.Paused,
            WorkerState.Failed => WorkConcurrencyCapacityBucket.Failed,
            _ => null,
        };

        return bucket is not null;
    }

    private TimeSpan? ResolveFailedWorkerAutoCancelDelayLocked()
        => this.failedWorkerAutoCancelOverride.Mode switch
        {
            FailedWorkerAutoCancelOverrideMode.Manual => null,
            FailedWorkerAutoCancelOverrideMode.Configured => this.Configuration.FailedWorker.AutoCancelAfter,
            FailedWorkerAutoCancelOverrideMode.Explicit => this.failedWorkerAutoCancelOverride.AutoCancelAfter,
            _ => this.Configuration.FailedWorker.Handling == WorkFailedWorkerHandling.AutoCancel
                ? this.Configuration.FailedWorker.AutoCancelAfter
                : null,
        };

    private void ReleaseExecutionCancellationLocked()
    {
        this.executionCancellation?.Dispose();
        this.executionCancellation = null;
    }

    private void SignalRecurrenceWaitLocked()
        => this.recurrenceWaitSignal.TrySetResult();

    public WorkCompletion ToCompletion(WorkCompletionStatus status)
    {
        lock (this.sync)
        {
            return this.ToCompletionLocked(status);
        }
    }

    private WorkCompletion ToCompletionLocked(WorkCompletionStatus status)
        => new(status, this.ToSnapshotLocked(), this.Output, this.Messages);

    public WorkEventMetadata ToEventMetadata(
        WorkSystemId workSystemId,
        string eventType)
        => new(
            workSystemId,
            this.Id,
            this.Work.Definition.Id,
            this.Work.Definition.Name,
            this.SubjectId,
            this.ConcurrencyKey,
            eventType,
            this.GetIdentifierSnapshot);

    public WorkEvent ToEvent(
        WorkSystemId workSystemId,
        string? workSystemName,
        string eventType,
        WorkerEventPayloadDetails? details = null)
    {
        lock (this.sync)
        {
            return new(
                DateTimeOffset.UtcNow,
                workSystemId,
                workSystemName,
                this.Id,
                this.Work.Definition.Id,
                this.Work.Definition.Name,
                this.SubjectId,
                this.ConcurrencyKey,
                this.identifiers.ToHashSet(),
                eventType,
                this.CreateEventDataLocked(details));
        }
    }

    public WorkEvent ToLogEvent(
        WorkSystemId workSystemId,
        string? workSystemName,
        WorkerLogEntry entry)
    {
        lock (this.sync)
        {
            return new(
                entry.OccurredAt,
                workSystemId,
                workSystemName,
                this.Id,
                this.Work.Definition.Id,
                this.Work.Definition.Name,
                this.SubjectId,
                this.ConcurrencyKey,
                this.identifiers.ToHashSet(),
                "worker.log",
                this.CreateEventDataLocked(new WorkerEventPayloadDetails(
                    IncludeLatestIteration: true,
                    LogEntry: entry)));
        }
    }

    private IReadOnlySet<WorkIdentifier> GetIdentifierSnapshot()
    {
        lock (this.sync)
        {
            return this.identifiers.ToHashSet();
        }
    }

    private WorkerSummary ToSummaryLocked()
        => new(
            this.Id,
            this.Revision,
            this.StateSequence,
            this.Work.Definition.Name,
            this.Work.Definition.Category,
            this.SubjectId,
            this.ConcurrencyKey,
            this.identifiers.ToHashSet(),
            this.RequestContext,
            this.State,
            this.interruptionReason,
            this.CreatedAt,
            this.StateChangedAt,
            this.UpdatedAt)
        {
            RetryAttempt = this.retryAttempt,
            QueueDuration = this.QueueDurationLocked(),
            TotalExecutionDuration = this.TotalExecutionDurationLocked(),
            NextRunAt = this.nextRunAt,
            ConfigDifferenceCount = WorkerConfigurationDifferenceCounter.CountDifferences(
                this.Options,
                this.Configuration,
                this.Work.Definition.DefaultOptions,
                this.Work.Definition.Configuration),
        };

    private WorkerOverviewItem ToOverviewItemLocked()
        => new(
            this.Id,
            this.Work.Definition.Name,
            this.SubjectId,
            this.ConcurrencyKey,
            this.identifiers.ToHashSet(),
            this.Revision,
            this.Work.Definition.Category,
            this.State,
            this.interruptionReason,
            this.CreatedAt,
            this.StateChangedAt,
            this.UpdatedAt)
        {
            QueueDuration = this.QueueDurationLocked(),
            TotalExecutionDuration = this.TotalExecutionDurationLocked(),
            NextRunAt = this.nextRunAt,
        };

    private System.Text.Json.JsonElement CreateEventDataLocked(WorkerEventPayloadDetails? details)
    {
        details ??= new WorkerEventPayloadDetails();
        var latestIteration = details.IncludeLatestIteration
            ? this.GetLatestIterationLocked()
            : null;
        return WorkerEventPayloads.Create(
            this.ToSummaryLocked(),
            this.CreateEventKeysLocked(),
            details.RequestContext,
            details.Action,
            details.ActionStatus,
            details.ReconfigurationStatus,
            details.Reconfiguration,
            details.CompletionStatus,
            latestIteration,
            details.RecurrenceInterval,
            details.RetryDelay,
            details.LogEntry,
            details.IncludeRetainedSummaries ? this.CreateRetainedLogSummaryLocked() : null,
            details.IncludeRetainedSummaries ? this.CreateRetainedTimelineSummaryLocked() : null);
    }

    private IReadOnlyList<WorkerEventKey> CreateEventKeysLocked()
    {
        var keys = new List<WorkerEventKey>();
        if (this.SubjectId is { } subjectId)
        {
            keys.Add(new WorkerEventKey(WorkKeyKind.Subject, subjectId.Type, subjectId.Value));
        }

        if (this.ConcurrencyKey is { } concurrencyKey)
        {
            keys.Add(new WorkerEventKey(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value));
        }

        keys.AddRange(this.identifiers.Select(identifier =>
            new WorkerEventKey(WorkKeyKind.Identifier, identifier.Type, identifier.Value)));
        return keys;
    }

    private WorkerIterationSnapshot? GetLatestIterationLocked()
    {
        if (this.currentIteration is not null)
        {
            return this.CreateCurrentIterationSnapshotLocked(DateTimeOffset.UtcNow);
        }

        return this.retainedIterations
            .OrderByDescending(iteration => iteration.Sequence)
            .FirstOrDefault();
    }

    private WorkerIterationSnapshot? GetIterationSnapshotLocked(long sequence)
    {
        if (this.currentIteration?.Sequence == sequence)
        {
            return this.CreateCurrentIterationSnapshotLocked(DateTimeOffset.UtcNow);
        }

        return this.retainedIterations
            .FirstOrDefault(iteration => iteration.Sequence == sequence);
    }

    private long? GetLatestTrackedIterationSequenceLocked()
        => this.currentIteration?.Sequence ??
            this.retainedIterations
                .OrderByDescending(iteration => iteration.Sequence)
                .Select(iteration => (long?)iteration.Sequence)
                .FirstOrDefault();

    private WorkerEventRetainedLogSummary CreateRetainedLogSummaryLocked()
        => new(
            this.logSummaryTotalCount,
            this.logSummaryCriticalCount,
            this.logSummaryErrorCount,
            this.logSummaryCriticalCount + this.logSummaryErrorCount,
            this.logSummaryWarningCount,
            this.logSummaryWarningCount,
            this.logSummaryInformationCount,
            this.logSummaryDebugCount,
            this.logSummaryTraceCount);

    private WorkerEventRetainedTimelineSummary CreateRetainedTimelineSummaryLocked()
    {
        var waitingRowCount = this.State == WorkerState.Waiting ? 1 : 0;
        var stateRowCount = this.timelineSyntheticStateRowCandidateCount - this.GetSuppressedTimelineStateRowCountLocked();
        return new WorkerEventRetainedTimelineSummary(
            this.timelineActionTotalCount +
            stateRowCount +
            this.timelineIterationSystemCount +
            this.timelineIterationFailureCount +
            waitingRowCount,
            this.timelineActionUserCount,
            this.timelineActionSystemCount +
            stateRowCount +
            this.timelineIterationSystemCount +
            waitingRowCount,
            this.timelineIterationFailureCount);
    }

    private void AddLogSummaryEntryLocked(WorkerLogEntry entry)
    {
        this.logSummaryTotalCount++;
        switch (entry.Level)
        {
            case LogLevel.Critical:
                this.logSummaryCriticalCount++;
                break;
            case LogLevel.Error:
                this.logSummaryErrorCount++;
                break;
            case LogLevel.Warning:
                this.logSummaryWarningCount++;
                break;
            case LogLevel.Information:
                this.logSummaryInformationCount++;
                break;
            case LogLevel.Debug:
                this.logSummaryDebugCount++;
                break;
            case LogLevel.Trace:
                this.logSummaryTraceCount++;
                break;
        }
    }

    private void RemoveLogSummaryEntryLocked(WorkerLogEntry entry)
    {
        this.logSummaryTotalCount--;
        switch (entry.Level)
        {
            case LogLevel.Critical:
                this.logSummaryCriticalCount--;
                break;
            case LogLevel.Error:
                this.logSummaryErrorCount--;
                break;
            case LogLevel.Warning:
                this.logSummaryWarningCount--;
                break;
            case LogLevel.Information:
                this.logSummaryInformationCount--;
                break;
            case LogLevel.Debug:
                this.logSummaryDebugCount--;
                break;
            case LogLevel.Trace:
                this.logSummaryTraceCount--;
                break;
        }
    }

    private void AddTimelineActionEntryLocked(WorkerActionHistoryEntry entry)
    {
        this.timelineActionTotalCount++;
        if (HasActor(entry.Origin))
        {
            this.timelineActionUserCount++;
        }
        else
        {
            this.timelineActionSystemCount++;
        }

        if (entry.Status != WorkActionStatus.Accepted ||
            entry.State is not (WorkerState.Paused or WorkerState.Canceled))
        {
            return;
        }

        this.timelineSyntheticStateRowCandidateCount++;
        var key = (entry.State, entry.StateSequence);
        this.suppressedTimelineStateRowCounts[key] = this.suppressedTimelineStateRowCounts.GetValueOrDefault(key) + 1;
    }

    private void RemoveTimelineActionEntryLocked(WorkerActionHistoryEntry entry)
    {
        this.timelineActionTotalCount--;
        if (HasActor(entry.Origin))
        {
            this.timelineActionUserCount--;
        }
        else
        {
            this.timelineActionSystemCount--;
        }

        if (entry.Status != WorkActionStatus.Accepted ||
            entry.State is not (WorkerState.Paused or WorkerState.Canceled))
        {
            return;
        }

        this.timelineSyntheticStateRowCandidateCount--;
        var key = (entry.State, entry.StateSequence);
        if (!this.suppressedTimelineStateRowCounts.TryGetValue(key, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            this.suppressedTimelineStateRowCounts.Remove(key);
        }
        else
        {
            this.suppressedTimelineStateRowCounts[key] = count - 1;
        }
    }

    private void RemoveActionHistoryForIterationLocked(long iterationSequence)
    {
        for (var index = this.actionHistory.Count - 1; index >= 0; index--)
        {
            var entry = this.actionHistory[index];
            if (entry.IterationSequence != iterationSequence)
            {
                continue;
            }

            this.actionHistory.RemoveAt(index);
            this.RemoveTimelineActionEntryLocked(entry);
        }
    }

    private void AddTimelineIterationEntryLocked(WorkCompletionStatus status)
    {
        if (status == WorkCompletionStatus.Failed)
        {
            this.timelineIterationFailureCount++;
            return;
        }

        this.timelineIterationSystemCount++;
    }

    private void RemoveTimelineIterationEntryLocked(WorkCompletionStatus status)
    {
        if (status == WorkCompletionStatus.Failed)
        {
            this.timelineIterationFailureCount--;
            return;
        }

        this.timelineIterationSystemCount--;
    }

    private int GetSuppressedTimelineStateRowCountLocked()
        => this.State is WorkerState.Paused or WorkerState.Canceled
            ? this.suppressedTimelineStateRowCounts.GetValueOrDefault((this.State, this.StateSequence))
            : 0;

    private static bool HasActor(WorkOrigin origin)
        => !string.IsNullOrWhiteSpace(origin.Actor.Name) ||
            !string.IsNullOrWhiteSpace(origin.Actor.Id);

    private TimeSpan? QueueDurationLocked()
        => this.firstStartedAt is null ? null : this.firstStartedAt.Value - this.CreatedAt;

    private TimeSpan TotalExecutionDurationLocked()
        => this.currentIteration is { } iteration
            ? this.totalExecutionDuration + (DateTimeOffset.UtcNow - iteration.StartedAt)
            : this.totalExecutionDuration;

    private sealed record CurrentWorkerIteration(long Sequence, DateTimeOffset StartedAt, int AttemptCount)
    {
        public List<WorkerLogEntry> Logs { get; } = [];

        public long NextLogOrdinal { get; set; }
    }

    private readonly record struct CheckedWorkerTransition(
        WorkerStateTransition? Transition,
        WorkActionOutcome? Rejection)
    {
        public WorkerStateTransition RequiredTransition
            => this.Transition ?? throw new InvalidOperationException("Checked worker transition did not include an accepted transition.");

        public static CheckedWorkerTransition Accepted(WorkerStateTransition transition)
            => new(transition, null);

        public static CheckedWorkerTransition Rejected(WorkActionOutcome rejection)
            => new(null, rejection);
    }
}

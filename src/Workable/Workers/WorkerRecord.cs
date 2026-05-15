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
    WorkOrigin origin,
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
    private readonly List<WorkerIterationSnapshot> successfulIterations = [];
    private readonly List<WorkerIterationSnapshot> failedIterations = [];
    private readonly List<WorkerIterationSnapshot> interruptedIterations = [];
    private readonly List<WorkerLogEntry> logEntries = [];
    private readonly List<WorkerActionHistoryEntry> actionHistory = [];
    private readonly HashSet<WorkIdentifier> identifiers = input?.Identifiers?.ToHashSet() ?? [];
    private readonly HashSet<WorkInitializationId> completedInitializers = [];
    private WorkProfileSnapshot? profile;
    private WorkProfileSnapshot? pendingIterationProfile;
    private CurrentWorkerIteration? currentIteration;
    private DateTimeOffset? firstStartedAt;
    private DateTimeOffset? nextRunAt;
    private TimeSpan totalExecutionDuration;
    private long? lastIterationSequence;
    private long iterationSequence;

    public WorkerId Id { get; } = id;

    public long Revision { get; private set; }

    public long StateSequence { get; private set; }

    public RegisteredWork Work { get; } = work;

    public WorkInput? Input { get; } = input;

    public WorkSubjectId? SubjectId => this.Input?.SubjectId;

    public WorkConcurrencyKey? ConcurrencyKey => this.Input?.ConcurrencyKey;

    public WorkOrigin Origin { get; } = origin;

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

    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;

    public Action<WorkerRecord, WorkerIterationSnapshot>? IterationRecorded { get; set; }

    public Action<WorkerRecord, WorkerIterationReference>? IterationForgotten { get; set; }

    public bool IsFinal => WorkerStateMachine.IsFinal(this.State);

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
        WorkerIterationSnapshot? currentIterationSnapshot = null;
        Action<WorkerRecord, WorkerIterationSnapshot>? iterationRecorded = null;
        lock (this.sync)
        {
            if (!this.identifiers.Add(identifier))
            {
                return false;
            }

            this.MarkUpdated();
            if (this.currentIteration is not null)
            {
                currentIterationSnapshot = this.CreateCurrentIterationSnapshotLocked(DateTimeOffset.UtcNow);
                iterationRecorded = this.IterationRecorded;
            }
        }

        if (currentIterationSnapshot is not null)
        {
            iterationRecorded?.Invoke(this, currentIterationSnapshot);
        }

        return true;
    }

    public bool IsInitializationComplete(WorkInitializationId initializationId)
    {
        lock (this.sync)
        {
            return this.completedInitializers.Contains(initializationId);
        }
    }

    public void MarkInitializationComplete(WorkInitializationId initializationId)
    {
        lock (this.sync)
        {
            if (this.completedInitializers.Add(initializationId))
            {
                this.MarkUpdated();
            }
        }
    }

    public WorkActionOutcome Start(CancellationToken cancellationToken, long expectedRevision, bool advancesRevision, out CancellationToken executionToken)
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
            executionToken = this.executionCancellation.Token;
            this.ApplyAcceptedTransitionLocked(transition, advancesRevision);
            this.IsStartDeferred = false;
            this.Output = null;
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

    public WorkActionOutcome RequestCancel(long expectedRevision)
    {
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

            this.State = transition.NextState;
            this.Output = null;
            this.RecordIterationLocked(null, this.Messages, transition.CompletionStatus);
            this.nextRunAt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
            return transition.CompletionStatus;
        }
    }

    public WorkActionOutcome ForceCancelForSystemStop()
    {
        lock (this.sync)
        {
            if (this.IsFinal)
            {
                return WorkActionOutcome.Invalid(
                    WorkAction.Cancel,
                    this.ToSnapshotLocked(),
                    [WorkMessage.Error("workable.worker.final", $"Worker cannot be force-canceled from state '{this.State}'.", "worker")]);
            }

            this.Messages =
            [
                .. this.Messages,
                WorkMessage.Warning(
                    "workable.worker.shutdown_forced",
                    "Worker did not stop within the shutdown grace period and was force-canceled by Workable.",
                    "worker"),
            ];
            this.State = WorkerState.Canceled;
            this.Output = null;
            this.RecordIterationLocked(null, this.Messages, WorkCompletionStatus.Canceled);
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
            this.SignalRecurrenceWaitLocked();

            return WorkActionOutcome.Accepted(
                WorkAction.Cancel,
                this.ToSnapshotLocked(),
                [WorkMessage.Warning(
                    "workable.worker.shutdown_forced",
                    "Worker did not stop within the shutdown grace period and was force-canceled by Workable.",
                    "worker")]);
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

            this.State = WorkerState.Waiting;
            this.Output = result.Output;
            this.Messages = result.Messages;
            this.recurrenceWaitSignal = CreateSignalSource();
            this.nextRunAt = DateTimeOffset.UtcNow + this.Configuration.Recurrence.Interval;
            this.RecordIterationLocked(result, result.HasErrors ? WorkCompletionStatus.Failed : WorkCompletionStatus.Completed);
            this.AdvanceStateSequence();
            return WorkCompletionStatus.Invalid;
        }
    }

    public WorkCompletionStatus CompleteRetryIteration(WorkExecutionResult result, TimeSpan retryDelay)
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

            this.State = WorkerState.Retrying;
            this.Output = result.Output;
            this.Messages = result.Messages;
            this.recurrenceWaitSignal = CreateSignalSource();
            this.nextRunAt = DateTimeOffset.UtcNow + retryDelay;
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

            var status = this.Messages.Any(message => message.Severity == WorkMessageSeverity.Error)
                ? WorkCompletionStatus.Failed
                : WorkCompletionStatus.Completed;
            this.State = status == WorkCompletionStatus.Failed
                ? WorkerState.Failed
                : WorkerState.Completed;
            this.nextRunAt = null;
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

            this.State = WorkerState.Running;
            this.Output = null;
            this.Messages = [];
            this.nextRunAt = null;
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

            this.State = WorkerState.Running;
            this.Output = null;
            this.Messages = [];
            this.nextRunAt = null;
            this.BeginIterationLocked();
            this.AdvanceStateSequence();
            return true;
        }
    }

    public void Fail(WorkMessage message)
    {
        lock (this.sync)
        {
            this.Messages = [message];
            this.State = WorkerState.Failed;
            this.RecordIterationLocked(null, this.Messages, WorkCompletionStatus.Failed);
            this.nextRunAt = null;
            this.AdvanceStateSequence();
            this.ReleaseExecutionCancellationLocked();
        }
    }

    public WorkActionOutcome Reconfigure(WorkerReconfiguration changes, long expectedRevision)
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

            if (changes.Concurrency is not null)
            {
                configuration = configuration with
                {
                    Concurrency = changes.Concurrency,
                };
            }

            if (changes.Start is not null)
            {
                configuration = configuration with
                {
                    Start = changes.Start,
                };
            }

            if (changes.Idempotency is not null)
            {
                configuration = configuration with
                {
                    Idempotency = changes.Idempotency,
                };
            }

            var configurationErrors = WorkConfigurationValidator.Validate(configuration);
            if (configurationErrors.Count > 0)
            {
                return WorkActionOutcome.Invalid(WorkAction.Start, this.ToSnapshotLocked(), configurationErrors);
            }

            var concurrencyInputErrors = WorkConfigurationValidator.ValidateConcurrencyInput(
                concurrency: configuration.Concurrency,
                input: this.Input);
            if (concurrencyInputErrors.Count > 0)
            {
                return WorkActionOutcome.Invalid(WorkAction.Start, this.ToSnapshotLocked(), concurrencyInputErrors);
            }

            this.Options = options;
            this.Configuration = configuration;
            if (!this.Configuration.Concurrency.IsEnabled)
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

    public bool ShouldCaptureLog(LogLevel level)
    {
        lock (this.sync)
        {
            return level != LogLevel.None &&
                this.Configuration.Logging.IsEnabled &&
                level >= this.Configuration.Logging.Level;
        }
    }

    public void RecordLog(WorkerLogEntry entry)
    {
        lock (this.sync)
        {
            var logging = this.Configuration.Logging;
            if (!logging.IsEnabled || entry.Level < logging.Level || logging.MaximumBufferedEntries <= 0)
            {
                return;
            }

            this.logEntries.Add(entry);
            this.currentIteration?.Logs.Add(entry);
            while (this.logEntries.Count > logging.MaximumBufferedEntries)
            {
                this.logEntries.RemoveAt(0);
            }

            this.MarkUpdated();
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

    public void RecordActionHistory(WorkActionOutcome outcome, WorkOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(origin);

        lock (this.sync)
        {
            this.actionHistory.Add(new WorkerActionHistoryEntry(
                DateTimeOffset.UtcNow,
                WorkerActionHistoryKind.WorkerAction,
                outcome.Action,
                outcome.Status,
                origin,
                this.Revision,
                this.StateSequence,
                outcome.Messages));
            this.MarkUpdated();
        }
    }

    public void RecordReconfigurationHistory(
        WorkerReconfiguration reconfiguration,
        WorkActionOutcome outcome,
        WorkOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(reconfiguration);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(origin);

        lock (this.sync)
        {
            this.actionHistory.Add(new WorkerActionHistoryEntry(
                DateTimeOffset.UtcNow,
                WorkerActionHistoryKind.Reconfiguration,
                null,
                outcome.Status,
                origin,
                this.Revision,
                this.StateSequence,
                outcome.Messages,
                reconfiguration));
            this.MarkUpdated();
        }
    }

    public bool CountsAgainstConcurrencyCapacity(WorkConcurrencyBlockingMode blockingMode)
    {
        lock (this.sync)
        {
            if (!this.Configuration.Concurrency.IsEnabled)
            {
                return false;
            }

            if (this.State == WorkerState.Queued)
            {
                return this.ShouldStartAutomatically() && !this.IsStartDeferred;
            }

            return blockingMode switch
            {
                WorkConcurrencyBlockingMode.WhileExecuting => this.State is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling,
                WorkConcurrencyBlockingMode.WhileExecutingOrPaused => this.State is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling or WorkerState.Paused,
                WorkConcurrencyBlockingMode.WhileExecutingOrFailed => this.State is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling or WorkerState.Failed,
                WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed => this.State is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Pausing or WorkerState.Canceling or WorkerState.Paused or WorkerState.Failed,
                _ => false,
            };
        }
    }

    public bool IsDeferredConcurrencyStartFor(WorkDefinitionId definitionId)
    {
        lock (this.sync)
        {
            return this.Work.Definition.Id == definitionId &&
                this.Configuration.Concurrency.IsEnabled &&
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
                !this.Configuration.Concurrency.IsEnabled;
        }
    }

    public bool ShouldStartWithConcurrency()
    {
        lock (this.sync)
        {
            return this.State == WorkerState.Queued &&
                this.ShouldStartAutomatically() &&
                this.Configuration.Concurrency.IsEnabled &&
                !this.IsStartDeferred;
        }
    }

    public void DeferConcurrencyStart()
    {
        lock (this.sync)
        {
            if (this.State == WorkerState.Queued && this.Configuration.Concurrency.IsEnabled)
            {
                this.IsStartDeferred = true;
            }
        }
    }

    private WorkerSnapshot ToSnapshotLocked()
    {
        var iterations = this.successfulIterations
            .Concat(this.failedIterations)
            .Concat(this.interruptedIterations)
            .OrderBy(iteration => iteration.Sequence)
            .ToArray();

        var latestIteration = this.GetLatestIterationLocked();
        return new(
            this.Id,
            this.Revision,
            this.StateSequence,
            this.Work.Definition.Id,
            this.Work.Definition.Name,
            this.Work.Definition.Category,
            this.SubjectId,
            this.ConcurrencyKey,
            this.identifiers.ToHashSet(),
            this.Origin,
            this.State,
            this.Input,
            this.Output,
            this.Options,
            this.Configuration,
            this.Messages,
            this.CreatedAt,
            this.UpdatedAt)
        {
            Iterations = iterations,
            LastIteration = latestIteration,
            CurrentIterationSequence = this.currentIteration?.Sequence,
            LastIterationSequence = this.lastIterationSequence,
            Logs = [.. this.logEntries],
            ActionHistory = [.. this.actionHistory],
            Profile = this.profile,
            QueueDuration = this.QueueDurationLocked(),
            TotalExecutionDuration = this.TotalExecutionDurationLocked(),
            NextRunAt = this.nextRunAt,
        };
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

        this.State = transition.NextState;
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
            output,
            messages)
        {
            Logs = [.. iterationInProgress.Logs],
            Profile = this.pendingIterationProfile,
        };
        this.pendingIterationProfile = null;
        this.currentIteration = null;
        this.lastIterationSequence = iteration.Sequence;
        this.IterationRecorded?.Invoke(this, iteration);
        var retained = status switch
        {
            WorkCompletionStatus.Completed => this.successfulIterations,
            WorkCompletionStatus.Failed => this.failedIterations,
            _ => this.interruptedIterations,
        };
        retained.Add(iteration);

        var maximum = status == WorkCompletionStatus.Completed
            ? this.Configuration.Recurrence.RetainedSuccessfulIterations
            : this.Configuration.Recurrence.RetainedFailedIterations;
        while (retained.Count > maximum)
        {
            var forgotten = retained[0];
            retained.RemoveAt(0);
            this.IterationForgotten?.Invoke(this, new WorkerIterationReference(this.Id, forgotten.Sequence));
        }
    }

    private void BeginIterationLocked()
    {
        var startedAt = DateTimeOffset.UtcNow;
        this.currentIteration = new CurrentWorkerIteration(++this.iterationSequence, startedAt);
        this.pendingIterationProfile = null;
        this.firstStartedAt ??= startedAt;
        this.IterationRecorded?.Invoke(this, this.CreateCurrentIterationSnapshotLocked(startedAt));
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

    public void DisposeExecutionResources()
    {
        lock (this.sync)
        {
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
            this.State = transition.RequiredNextState;
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
            WorkActionStatus.Conflict => WorkActionOutcome.Conflict(transition.Action, this.ToSnapshotLocked(), [this.ToMessage(transition)]),
            _ => WorkActionOutcome.Invalid(transition.Action, this.ToSnapshotLocked(), [this.ToMessage(transition)]),
        };

    private WorkMessage ToMessage(WorkerStateTransition transition)
    {
        if (transition.MessageCode is null || transition.MessageText is null)
        {
            throw new InvalidOperationException("Rejected worker transition did not include message details.");
        }

        return WorkMessage.Error(transition.MessageCode, transition.MessageText, "worker");
    }

    public WorkActionOutcome RequestCancelForSystemStop()
    {
        CancellationTokenSource? cancellation;
        WorkActionOutcome outcome;

        lock (this.sync)
        {
            var transition = WorkerStateMachine.Apply(this.State, WorkAction.Cancel);
            if (!transition.IsAccepted)
            {
                return this.ToOutcomeLocked(transition);
            }

            this.ApplyAcceptedTransitionLocked(transition, advancesRevision: false);
            cancellation = transition.CancelsExecution ? this.executionCancellation : null;
            this.SignalRecurrenceWaitLocked();
            outcome = this.ToOutcomeLocked(transition);
        }

        CancelIfAvailable(cancellation);

        return outcome;
    }

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

    private void AdvanceStateSequence()
    {
        this.StateSequence++;
        this.MarkUpdated();
    }

    private void MarkUpdated()
        => this.UpdatedAt = DateTimeOffset.UtcNow;

    private bool ShouldStartAutomatically()
        => this.Configuration.Start.Policy != WorkStartPolicy.DoNotStart;

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

    public WorkEvent ToEvent(
        WorkSystemId workSystemId,
        string eventType,
        WorkOrigin? origin = null,
        WorkerEventPayloadDetails? details = null)
    {
        lock (this.sync)
        {
            return new(
                DateTimeOffset.UtcNow,
                workSystemId,
                this.Id,
                this.Work.Definition.Id,
                this.SubjectId,
                this.ConcurrencyKey,
                this.identifiers.ToHashSet(),
                origin ?? this.Origin,
                eventType,
                this.CreateEventDataLocked(details),
                this.Messages);
        }
    }

    public WorkEvent ToLogEvent(WorkSystemId workSystemId, WorkerLogEntry entry)
    {
        lock (this.sync)
        {
            return new(
                entry.OccurredAt,
                workSystemId,
                this.Id,
                this.Work.Definition.Id,
                this.SubjectId,
                this.ConcurrencyKey,
                this.identifiers.ToHashSet(),
                this.Origin,
                "worker.log",
                this.CreateEventDataLocked(new WorkerEventPayloadDetails(Log: entry)),
                [new WorkMessage(
                    "workable.worker.log",
                    LogSeverity(entry.Level),
                    entry.Message,
                    "worker.log",
                    LogMetadata(entry))]);
        }
    }

    private WorkerSummary ToSummaryLocked()
        => new(
            this.Id,
            this.Revision,
            this.StateSequence,
            this.Work.Definition.Id,
            this.Work.Definition.Name,
            this.Work.Definition.Category,
            this.SubjectId,
            this.ConcurrencyKey,
            this.identifiers.ToHashSet(),
            this.Origin,
            this.State,
            this.CreatedAt,
            this.UpdatedAt)
        {
            QueueDuration = this.QueueDurationLocked(),
            TotalExecutionDuration = this.TotalExecutionDurationLocked(),
            NextRunAt = this.nextRunAt,
        };

    private WorkerOverviewItem ToOverviewItemLocked()
        => new(
            this.Id,
            this.Work.Definition.Id,
            this.Work.Definition.Name,
            this.SubjectId,
            this.ConcurrencyKey,
            this.identifiers.ToHashSet(),
            this.Revision,
            this.Work.Definition.Category,
            this.State,
            this.CreatedAt,
            this.UpdatedAt)
        {
            QueueDuration = this.QueueDurationLocked(),
            TotalExecutionDuration = this.TotalExecutionDurationLocked(),
            NextRunAt = this.nextRunAt,
        };

    private System.Text.Json.JsonElement CreateEventDataLocked(WorkerEventPayloadDetails? details)
    {
        details ??= new WorkerEventPayloadDetails();
        return WorkerEventPayloads.Create(
            this.ToSummaryLocked(),
            details.IncludeInput ? this.Input : null,
            details.IncludeOutput ? this.Output : null,
            details.Action,
            details.ActionStatus,
            details.ReconfigurationStatus,
            details.Reconfiguration,
            details.CompletionStatus,
            details.IncludeLatestIteration ? this.GetLatestIterationLocked() : null,
            details.RecurrenceInterval,
            details.RetryDelay,
            details.Log);
    }

    private WorkerIterationSnapshot? GetLatestIterationLocked()
        => this.successfulIterations
            .Concat(this.failedIterations)
            .Concat(this.interruptedIterations)
            .OrderByDescending(iteration => iteration.Sequence)
            .FirstOrDefault();

    private TimeSpan? QueueDurationLocked()
        => this.firstStartedAt is null ? null : this.firstStartedAt.Value - this.CreatedAt;

    private TimeSpan TotalExecutionDurationLocked()
        => this.currentIteration is { } iteration
            ? this.totalExecutionDuration + (DateTimeOffset.UtcNow - iteration.StartedAt)
            : this.totalExecutionDuration;

    private sealed record CurrentWorkerIteration(long Sequence, DateTimeOffset StartedAt)
    {
        public List<WorkerLogEntry> Logs { get; } = [];
    }

    private static WorkMessageSeverity LogSeverity(LogLevel level)
        => level switch
        {
            LogLevel.Trace or LogLevel.Debug or LogLevel.Information => WorkMessageSeverity.Info,
            LogLevel.Warning => WorkMessageSeverity.Warning,
            _ => WorkMessageSeverity.Error,
        };

    private static IReadOnlyDictionary<string, object?> LogMetadata(WorkerLogEntry entry)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["category"] = entry.Category,
            ["level"] = entry.Level.ToString(),
            ["eventId"] = entry.EventId.Id,
            ["eventName"] = entry.EventId.Name,
            ["exceptionType"] = entry.ExceptionType,
            ["exceptionMessage"] = entry.ExceptionMessage,
        };

        if (entry.Metadata is not null)
        {
            foreach (var item in entry.Metadata)
            {
                metadata[$"state.{item.Key}"] = item.Value;
            }
        }

        return metadata;
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

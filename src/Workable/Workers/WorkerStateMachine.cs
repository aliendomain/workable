namespace Workable;

internal static class WorkerStateMachine
{
    public static WorkerStateTransition Apply(WorkerState state, WorkAction action)
    {
        if (IsTransitioning(state))
        {
            return WorkerStateTransition.Conflict(action, state);
        }

        return action switch
        {
            WorkAction.Start => state is WorkerState.Queued or WorkerState.Paused or WorkerState.Failed
                ? WorkerStateTransition.Accepted(action, state, WorkerState.Running)
                : WorkerStateTransition.Invalid(action, state, "workable.worker.not_startable", $"Worker cannot be started from state '{state}'."),

            WorkAction.Pause => state switch
            {
                WorkerState.Running => WorkerStateTransition.Accepted(action, state, WorkerState.Pausing, cancelsExecution: true),
                WorkerState.Waiting => WorkerStateTransition.Accepted(action, state, WorkerState.Paused),
                _ => WorkerStateTransition.Invalid(action, state, "workable.worker.not_pausable", $"Worker cannot be paused from state '{state}'."),
            },

            WorkAction.Cancel => IsFinal(state)
                ? WorkerStateTransition.Invalid(action, state, "workable.worker.final", $"Worker cannot be canceled from state '{state}'.")
                : state == WorkerState.Running
                    ? WorkerStateTransition.Accepted(action, state, WorkerState.Canceling, cancelsExecution: true)
                    : WorkerStateTransition.Accepted(action, state, WorkerState.Canceled),

            WorkAction.Push => state == WorkerState.Waiting
                ? WorkerStateTransition.Accepted(action, state, WorkerState.Queued)
                : WorkerStateTransition.Invalid(action, state, "workable.worker.not_waiting", $"Worker cannot be pushed from state '{state}'."),

            WorkAction.Purge => IsFinal(state)
                ? WorkerStateTransition.Accepted(action, state, nextState: null, removesWorker: true)
                : WorkerStateTransition.Invalid(action, state, "workable.worker.not_final", $"Worker cannot be purged from state '{state}'."),

            _ => WorkerStateTransition.Invalid(action, state, "workable.action.invalid", $"Action '{action}' is not supported."),
        };
    }

    public static WorkerCompletionTransition Complete(WorkerState state, bool hasErrors)
        => state switch
        {
            WorkerState.Pausing => new(WorkerState.Paused, WorkCompletionStatus.Paused),
            WorkerState.Canceling => new(WorkerState.Canceled, WorkCompletionStatus.Canceled),
            WorkerState.Running when hasErrors => new(WorkerState.Failed, WorkCompletionStatus.Failed),
            WorkerState.Running => new(WorkerState.Completed, WorkCompletionStatus.Completed),
            WorkerState.Canceled => new(WorkerState.Canceled, WorkCompletionStatus.Canceled),
            WorkerState.Completed => new(WorkerState.Completed, WorkCompletionStatus.Completed),
            WorkerState.Failed => new(WorkerState.Failed, WorkCompletionStatus.Failed),
            _ => new(state, WorkCompletionStatus.Invalid),
        };

    public static WorkerCompletionTransition CompleteCancellation(WorkerState state)
        => state switch
        {
            WorkerState.Pausing => new(WorkerState.Paused, WorkCompletionStatus.Paused),
            WorkerState.Running or WorkerState.Canceling => new(WorkerState.Canceled, WorkCompletionStatus.Canceled),
            WorkerState.Waiting => new(WorkerState.Canceled, WorkCompletionStatus.Canceled),
            _ => new(state, CompletionStatusFor(state)),
        };

    public static WorkCompletionStatus CompletionStatusFor(WorkerState state)
        => state switch
        {
            WorkerState.Paused => WorkCompletionStatus.Paused,
            WorkerState.Canceled => WorkCompletionStatus.Canceled,
            WorkerState.Completed => WorkCompletionStatus.Completed,
            WorkerState.Failed => WorkCompletionStatus.Failed,
            _ => WorkCompletionStatus.Invalid,
        };

    public static bool IsFinal(WorkerState state)
        => state is WorkerState.Canceled or WorkerState.Completed;

    public static bool IsTransitioning(WorkerState state)
        => state is WorkerState.Pausing or WorkerState.Canceling;
}

internal sealed record WorkerStateTransition(
    WorkAction Action,
    WorkerState CurrentState,
    WorkActionStatus Status,
    WorkerState? NextState,
    bool CancelsExecution,
    bool RemovesWorker,
    string? MessageCode,
    string? MessageText)
{
    public bool IsAccepted => this.Status == WorkActionStatus.Accepted;

    public WorkerState RequiredNextState
        => this.NextState ?? throw new InvalidOperationException("Accepted worker transition did not include a next state.");

    public static WorkerStateTransition Accepted(
        WorkAction action,
        WorkerState currentState,
        WorkerState? nextState,
        bool cancelsExecution = false,
        bool removesWorker = false)
        => new(action, currentState, WorkActionStatus.Accepted, nextState, cancelsExecution, removesWorker, null, null);

    public static WorkerStateTransition Invalid(WorkAction action, WorkerState currentState, string messageCode, string messageText)
        => new(action, currentState, WorkActionStatus.Invalid, null, false, false, messageCode, messageText);

    public static WorkerStateTransition Conflict(WorkAction action, WorkerState currentState)
        => new(
            action,
            currentState,
            WorkActionStatus.Conflict,
            null,
            false,
            false,
            "workable.worker.conflict",
            $"Worker is already processing another state change from state '{currentState}'.");
}

internal sealed record WorkerCompletionTransition(
    WorkerState NextState,
    WorkCompletionStatus CompletionStatus);

using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerStateMachineTests
{
    private static readonly WorkAction[] SupportedActions =
    [
        WorkAction.Start,
        WorkAction.Pause,
        WorkAction.Cancel,
        WorkAction.Push,
        WorkAction.Purge,
    ];

    public static TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool> ActionTransitions()
    {
        var data = new TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool>();
        foreach (var state in Enum.GetValues<WorkerState>())
        {
            AddExpectedTransitions(data, state);
        }

        return data;
    }

    [Fact]
    public void ActionTransitionRulesCoverEveryWorkerAction()
    {
        Assert.Equal(
            Enum.GetValues<WorkAction>().Order(),
            SupportedActions.Order());
    }

    [Theory]
    [MemberData(nameof(ActionTransitions))]
    public void ActionTransitionRulesAreExhaustive(
        WorkerState state,
        WorkAction action,
        WorkActionStatus expectedStatus,
        WorkerState? expectedNextState,
        bool expectedCancellation,
        bool expectedRemoval)
    {
        var transition = WorkerStateMachine.Apply(state, action);

        Assert.Equal(expectedStatus, transition.Status);
        Assert.Equal(expectedNextState, transition.NextState);
        Assert.Equal(expectedCancellation, transition.CancelsExecution);
        Assert.Equal(expectedRemoval, transition.RemovesWorker);
    }

    [Theory]
    [InlineData(WorkerState.Queued, false, WorkerState.Queued, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Queued, true, WorkerState.Queued, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Running, false, WorkerState.Completed, WorkCompletionStatus.Completed)]
    [InlineData(WorkerState.Running, true, WorkerState.Failed, WorkCompletionStatus.Failed)]
    [InlineData(WorkerState.Waiting, false, WorkerState.Waiting, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Waiting, true, WorkerState.Waiting, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Retrying, false, WorkerState.Retrying, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Retrying, true, WorkerState.Retrying, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Pausing, false, WorkerState.Paused, WorkCompletionStatus.Paused)]
    [InlineData(WorkerState.Pausing, true, WorkerState.Paused, WorkCompletionStatus.Paused)]
    [InlineData(WorkerState.Paused, false, WorkerState.Paused, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Paused, true, WorkerState.Paused, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Interrupting, false, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Interrupting, true, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Interrupted, false, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Interrupted, true, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Canceling, false, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Canceling, true, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Canceled, false, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Canceled, true, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Completed, false, WorkerState.Completed, WorkCompletionStatus.Completed)]
    [InlineData(WorkerState.Completed, true, WorkerState.Completed, WorkCompletionStatus.Completed)]
    [InlineData(WorkerState.Failed, false, WorkerState.Failed, WorkCompletionStatus.Failed)]
    [InlineData(WorkerState.Failed, true, WorkerState.Failed, WorkCompletionStatus.Failed)]
    public void CompletionTransitionRulesAreExplicit(
        WorkerState state,
        bool hasErrors,
        WorkerState expectedNextState,
        WorkCompletionStatus expectedStatus)
    {
        var transition = WorkerStateMachine.Complete(state, hasErrors);

        Assert.Equal(expectedNextState, transition.NextState);
        Assert.Equal(expectedStatus, transition.CompletionStatus);
    }

    [Theory]
    [InlineData(WorkerState.Queued, false)]
    [InlineData(WorkerState.Running, false)]
    [InlineData(WorkerState.Waiting, false)]
    [InlineData(WorkerState.Retrying, false)]
    [InlineData(WorkerState.Pausing, false)]
    [InlineData(WorkerState.Paused, false)]
    [InlineData(WorkerState.Interrupting, false)]
    [InlineData(WorkerState.Interrupted, false)]
    [InlineData(WorkerState.Canceling, false)]
    [InlineData(WorkerState.Canceled, true)]
    [InlineData(WorkerState.Completed, true)]
    [InlineData(WorkerState.Failed, false)]
    public void FinalStateRulesAreExplicit(WorkerState state, bool expected)
    {
        Assert.Equal(expected, WorkerStateMachine.IsFinal(state));
    }

    [Theory]
    [InlineData(WorkerState.Pausing, WorkerState.Paused, WorkCompletionStatus.Paused)]
    [InlineData(WorkerState.Running, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Canceling, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Retrying, WorkerState.Canceled, WorkCompletionStatus.Canceled)]
    [InlineData(WorkerState.Interrupted, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Queued, WorkerState.Queued, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Completed, WorkerState.Completed, WorkCompletionStatus.Completed)]
    public void CancellationCompletionRulesAreExplicit(
        WorkerState state,
        WorkerState expectedNextState,
        WorkCompletionStatus expectedStatus)
    {
        var transition = WorkerStateMachine.CompleteCancellation(state);

        Assert.Equal(expectedNextState, transition.NextState);
        Assert.Equal(expectedStatus, transition.CompletionStatus);
    }

    [Theory]
    [InlineData(WorkerState.Running, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Interrupting, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Waiting, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Retrying, WorkerState.Interrupted, WorkCompletionStatus.Interrupted)]
    [InlineData(WorkerState.Queued, WorkerState.Queued, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Completed, WorkerState.Completed, WorkCompletionStatus.Completed)]
    public void InterruptionCompletionRulesAreExplicit(
        WorkerState state,
        WorkerState expectedNextState,
        WorkCompletionStatus expectedStatus)
    {
        var transition = WorkerStateMachine.CompleteInterruption(state);

        Assert.Equal(expectedNextState, transition.NextState);
        Assert.Equal(expectedStatus, transition.CompletionStatus);
    }

    private static void AddExpectedTransitions(TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool> data, WorkerState state)
    {
        foreach (var action in SupportedActions)
        {
            AddExpectedTransition(data, state, action);
        }
    }

    private static void AddExpectedTransition(
        TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool> data,
        WorkerState state,
        WorkAction action)
    {
        switch (action)
        {
            case WorkAction.Start:
                Add(data, state, action, StartStatus(state), StartNextState(state));
                break;
            case WorkAction.Pause:
                Add(data, state, action, PauseStatus(state), PauseNextState(state), state == WorkerState.Running);
                break;
            case WorkAction.Cancel:
                Add(data, state, action, CancelStatus(state), CancelNextState(state), state == WorkerState.Running);
                break;
            case WorkAction.Push:
                Add(data, state, action, PushStatus(state), PushNextState(state));
                break;
            case WorkAction.Purge:
                Add(data, state, action, PurgeStatus(state), nextState: null, removesWorker: WorkerStateMachine.IsFinal(state));
                break;
            default:
                throw new InvalidOperationException($"Worker action '{action}' is not covered by transition tests.");
        }
    }

    private static void Add(
        TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool> data,
        WorkerState state,
        WorkAction action,
        WorkActionStatus status,
        WorkerState? nextState,
        bool cancelsExecution = false,
        bool removesWorker = false)
        => data.Add(
            state,
            action,
            status,
            status == WorkActionStatus.Accepted ? nextState : null,
            status == WorkActionStatus.Accepted && cancelsExecution,
            status == WorkActionStatus.Accepted && removesWorker);

    private static WorkActionStatus StartStatus(WorkerState state)
        => ConflictOr(state, state is WorkerState.Queued or WorkerState.Paused or WorkerState.Failed);

    private static WorkerState? StartNextState(WorkerState state)
        => state is WorkerState.Queued or WorkerState.Paused or WorkerState.Failed ? WorkerState.Running : null;

    private static WorkActionStatus PauseStatus(WorkerState state)
        => ConflictOr(state, state is WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying);

    private static WorkerState? PauseNextState(WorkerState state)
        => state switch
        {
            WorkerState.Running => WorkerState.Pausing,
            WorkerState.Waiting or WorkerState.Retrying => WorkerState.Paused,
            _ => null,
        };

    private static WorkActionStatus CancelStatus(WorkerState state)
        => ConflictOr(state, !WorkerStateMachine.IsFinal(state));

    private static WorkerState? CancelNextState(WorkerState state)
        => state switch
        {
            WorkerState.Running => WorkerState.Canceling,
            WorkerState.Queued or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Paused or WorkerState.Interrupted or WorkerState.Failed => WorkerState.Canceled,
            _ => null,
        };

    private static WorkActionStatus PushStatus(WorkerState state)
        => ConflictOr(state, state is WorkerState.Waiting or WorkerState.Retrying);

    private static WorkerState? PushNextState(WorkerState state)
        => state is WorkerState.Waiting or WorkerState.Retrying ? WorkerState.Queued : null;

    private static WorkActionStatus PurgeStatus(WorkerState state)
        => ConflictOr(state, WorkerStateMachine.IsFinal(state));

    private static WorkActionStatus ConflictOr(WorkerState state, bool accepted)
        => WorkerStateMachine.IsTransitioning(state)
            ? WorkActionStatus.Conflict
            : accepted
                ? WorkActionStatus.Accepted
                : WorkActionStatus.Invalid;
}

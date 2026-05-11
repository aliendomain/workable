using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerStateMachineTests
{
    public static TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool> ActionTransitions()
    {
        var data = new TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool>();
        foreach (var state in Enum.GetValues<WorkerState>())
        {
            AddExpectedTransitions(data, state);
        }

        return data;
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
    [InlineData(WorkerState.Pausing, false, WorkerState.Paused, WorkCompletionStatus.Paused)]
    [InlineData(WorkerState.Pausing, true, WorkerState.Paused, WorkCompletionStatus.Paused)]
    [InlineData(WorkerState.Paused, false, WorkerState.Paused, WorkCompletionStatus.Invalid)]
    [InlineData(WorkerState.Paused, true, WorkerState.Paused, WorkCompletionStatus.Invalid)]
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
    [InlineData(WorkerState.Pausing, false)]
    [InlineData(WorkerState.Paused, false)]
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

    private static void AddExpectedTransitions(TheoryData<WorkerState, WorkAction, WorkActionStatus, WorkerState?, bool, bool> data, WorkerState state)
    {
        Add(data, state, WorkAction.Start, StartStatus(state), StartNextState(state));
        Add(data, state, WorkAction.Pause, PauseStatus(state), PauseNextState(state), state == WorkerState.Running);
        Add(data, state, WorkAction.Cancel, CancelStatus(state), CancelNextState(state), state == WorkerState.Running);
        Add(data, state, WorkAction.Push, PushStatus(state), PushNextState(state));
        Add(data, state, WorkAction.Purge, PurgeStatus(state), nextState: null, removesWorker: WorkerStateMachine.IsFinal(state));
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
        => ConflictOr(state, state is WorkerState.Running or WorkerState.Waiting);

    private static WorkerState? PauseNextState(WorkerState state)
        => state switch
        {
            WorkerState.Running => WorkerState.Pausing,
            WorkerState.Waiting => WorkerState.Paused,
            _ => null,
        };

    private static WorkActionStatus CancelStatus(WorkerState state)
        => ConflictOr(state, !WorkerStateMachine.IsFinal(state));

    private static WorkerState? CancelNextState(WorkerState state)
        => state switch
        {
            WorkerState.Running => WorkerState.Canceling,
            WorkerState.Queued or WorkerState.Waiting or WorkerState.Paused or WorkerState.Failed => WorkerState.Canceled,
            _ => null,
        };

    private static WorkActionStatus PushStatus(WorkerState state)
        => ConflictOr(state, state == WorkerState.Waiting);

    private static WorkerState? PushNextState(WorkerState state)
        => state == WorkerState.Waiting ? WorkerState.Queued : null;

    private static WorkActionStatus PurgeStatus(WorkerState state)
        => ConflictOr(state, WorkerStateMachine.IsFinal(state));

    private static WorkActionStatus ConflictOr(WorkerState state, bool accepted)
        => WorkerStateMachine.IsTransitioning(state)
            ? WorkActionStatus.Conflict
            : accepted
                ? WorkActionStatus.Accepted
                : WorkActionStatus.Invalid;
}

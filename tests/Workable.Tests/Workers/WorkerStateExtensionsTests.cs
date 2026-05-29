using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerStateExtensionsTests
{
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
    public void IsFinalMatchesDomainExpectation(WorkerState state, bool expected)
    {
        Assert.Equal(expected, state.IsFinal());
    }
}

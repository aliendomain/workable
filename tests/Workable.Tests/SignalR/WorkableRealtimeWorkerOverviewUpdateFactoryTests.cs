namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeWorkerOverviewUpdateFactoryTests
{
    [Fact]
    public void CreateSnapshotReturnsTheCompleteCurrentState()
    {
        var workerId = WorkerId.New();
        var worker = new WorkWorkerOverviewWorker(
            workerId,
            Revision: 3,
            StateSequence: 4,
            WorkerState.Running,
            IsFinal: false,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            NextRunAt: null,
            RetryAttempt: null,
            new WorkWorkerOverviewOrigin(WorkInvocationChannel.HttpApi),
            "signalr.worker",
            "SignalR",
            [],
            ConfigDifferenceCount: 0);
        var latestIteration = new WorkWorkerOverviewLatestIteration(
            workerId,
            Sequence: 1,
            WorkCompletionStatus.Executing,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: null,
            ExecutionDuration: null,
            Output: null,
            Failure: null);
        var state = new WorkWorkerOverviewRealtimeState(
            worker,
            latestIteration,
            LogSummary: null,
            LogEntries: [],
            RecentIterations: [],
            TimelineSummary: null,
            TimelineItems: []);

        var update = WorkableRealtimeWorkerOverviewUpdateFactory.CreateSnapshot(state);

        Assert.Same(state.Worker, update.Worker);
        Assert.Same(state.LatestIteration, update.LatestIteration);
        Assert.Same(state.LogEntries, update.LogEntries);
        Assert.Same(state.RecentIterations, update.RecentIterations);
        Assert.Same(state.TimelineItems, update.TimelineItems);
        Assert.False(update.RequiresRefresh);
    }

    [Fact]
    public void CreateSnapshotRejectsNullState()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            WorkableRealtimeWorkerOverviewUpdateFactory.CreateSnapshot(null!));

        Assert.Equal("state", exception.ParamName);
    }
}

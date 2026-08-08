namespace Workable;

internal static class WorkableRealtimeWorkerOverviewUpdateFactory
{
    public static WorkWorkerOverviewRealtimeUpdate CreateSnapshot(WorkWorkerOverviewRealtimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new WorkWorkerOverviewRealtimeUpdate(
            DateTimeOffset.UtcNow,
            state.Worker,
            state.LatestIteration,
            state.LogSummary,
            state.LogEntries,
            state.RecentIterations,
            state.TimelineSummary,
            state.TimelineItems);
    }
}

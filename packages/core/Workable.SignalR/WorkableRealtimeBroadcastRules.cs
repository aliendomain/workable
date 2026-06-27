namespace Workable;

internal static class WorkableRealtimeBroadcastRules
{
    public static bool ShouldPublishView(
        bool requiresIntervalPublish,
        WorkableRealtimeViewVersion lastPublishedVersion,
        WorkableRealtimeViewVersion currentVersion)
        => requiresIntervalPublish || lastPublishedVersion != currentVersion;

    public static bool ShouldPublishDiagnosticsAlertChange(
        WorkableRealtimeDiagnosticsAlertState? previous,
        WorkableRealtimeDiagnosticsAlertState current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous == current)
        {
            return false;
        }

        return previous is not null || current.IsAlerting;
    }
}

internal readonly record struct WorkableRealtimeViewVersion(
    long ReadModelSequence,
    long WorkflowSequence);

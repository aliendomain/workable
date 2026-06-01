namespace Workable;

internal static class WorkableRealtimeBroadcastRules
{
    public static bool ShouldPublishView(
        bool requiresIntervalPublish,
        long lastPublishedSequence,
        long appliedSequence)
        => requiresIntervalPublish || lastPublishedSequence != appliedSequence;

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

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

    public static bool IsNonCriticalBroadcastException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException and
            not AppDomainUnloadedException and
            not BadImageFormatException and
            not CannotUnloadAppDomainException and
            not InvalidProgramException and
            not global::System.Threading.ThreadAbortException;
    }
}

internal readonly record struct WorkableRealtimeViewVersion(
    long ReadModelSequence,
    long WorkflowSequence);

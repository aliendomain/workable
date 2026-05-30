namespace Workable;

public static class WorkCompletionStatusExtensions
{
    public static bool IsFinal(this WorkCompletionStatus status)
        => status is
            WorkCompletionStatus.Completed or
            WorkCompletionStatus.Failed or
            WorkCompletionStatus.Interrupted or
            WorkCompletionStatus.Canceled or
            WorkCompletionStatus.Invalid or
            WorkCompletionStatus.NotFound;
}

namespace Workable;

/// <summary>
/// Provides helper methods for working with <see cref="WorkCompletionStatus"/>.
/// </summary>
public static class WorkCompletionStatusExtensions
{
    /// <summary>
    /// Determines whether a completion status is terminal.
    /// </summary>
    /// <param name="status">The completion status to inspect.</param>
    /// <returns><see langword="true"/> when the status is terminal; otherwise <see langword="false"/>.</returns>
    public static bool IsFinal(this WorkCompletionStatus status)
        => status is
            WorkCompletionStatus.Completed or
            WorkCompletionStatus.Failed or
            WorkCompletionStatus.Interrupted or
            WorkCompletionStatus.Canceled or
            WorkCompletionStatus.Invalid or
            WorkCompletionStatus.NotFound;
}

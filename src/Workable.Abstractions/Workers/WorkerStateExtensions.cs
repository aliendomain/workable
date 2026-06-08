namespace Workable;

/// <summary>
/// Provides helper methods for working with <see cref="WorkerState"/>.
/// </summary>
public static class WorkerStateExtensions
{
    /// <summary>
    /// Determines whether a worker state is terminal.
    /// </summary>
    /// <param name="state">The worker state to inspect.</param>
    /// <returns><see langword="true"/> when the state is terminal; otherwise <see langword="false"/>.</returns>
    public static bool IsFinal(this WorkerState state)
        => state is WorkerState.Canceled or WorkerState.Completed;
}

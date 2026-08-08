namespace Workable;

/// <summary>
/// Exposes ordered status items emitted by work iterations.
/// </summary>
public interface IWorkIterationStatusStream
{
    /// <summary>
    /// Subscribes to one iteration, replaying retained items after the supplied sequence before continuing live.
    /// </summary>
    /// <param name="iteration">The iteration to observe.</param>
    /// <param name="afterSequence">
    /// The last sequence already received, or zero to request sequence one. A cursor older than retained replay reports
    /// a <see cref="WorkIterationStatusGapException"/> rather than silently starting at the oldest available item.
    /// </param>
    /// <returns>An ordered iteration status subscription.</returns>
    /// <exception cref="WorkIterationStatusGapException">
    /// The requested sequence is older than the retained replay window.
    /// </exception>
    /// <exception cref="WorkIterationStatusSubscriptionLimitException">
    /// The per-iteration or system-wide active subscription limit has been reached.
    /// </exception>
    IWorkIterationStatusSubscription Subscribe(
        WorkerIterationReference iteration,
        long afterSequence = 0);
}

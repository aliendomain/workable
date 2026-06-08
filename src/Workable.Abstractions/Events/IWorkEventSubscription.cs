namespace Workable;
/// <summary>
/// Represents one live event-stream subscription.
/// </summary>
public interface IWorkEventSubscription : IAsyncDisposable
{
    /// <summary>
    /// Reads events from the subscription until the subscription is disposed or the read is canceled.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the asynchronous read loop.</param>
    /// <returns>An asynchronous sequence of matching work events.</returns>
    IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default);
}
